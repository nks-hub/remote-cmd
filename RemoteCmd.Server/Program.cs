using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 200_000_000); // 200MB

// Parse arguments: <token> [--no-tls]
var token = args.Length > 0 && !args[0].StartsWith("-") ? args[0] : Guid.NewGuid().ToString("N")[..12];
var noTls = args.Contains("--no-tls");

if (noTls)
{
    builder.WebHost.UseUrls("http://0.0.0.0:7890");
}
else
{
    // Generate self-signed certificate for HTTPS
    var cert = GenerateSelfSignedCert();
    var certPath = Path.Combine(AppContext.BaseDirectory, "remotecmd.pfx");
    var certPassword = Guid.NewGuid().ToString("N")[..16];
    File.WriteAllBytes(certPath, cert.Export(X509ContentType.Pfx, certPassword));

    builder.WebHost.UseUrls("https://0.0.0.0:7890");
    builder.WebHost.ConfigureKestrel(o =>
    {
        o.Limits.MaxRequestBodySize = 200_000_000;
        o.ConfigureHttpsDefaults(https =>
        {
            https.ServerCertificate = new X509Certificate2(certPath, certPassword);
        });
    });
}

var app = builder.Build();

// Initialize AES-256-GCM encryption from token
Crypto.Init(token);

string? pendingCommand = null;
TaskCompletionSource<CommandResult>? resultTcs = null;
DateTime lastClientPoll = DateTime.MinValue;
var commandLock = new SemaphoreSlim(1, 1);

// File transfer state
FileTransfer? pendingUpload = null;
TaskCompletionSource<bool>? uploadTcs = null;
FileTransfer? pendingDownload = null;
TaskCompletionSource<FileTransfer>? downloadTcs = null;

var protocol = noTls ? "http" : "https";
Console.WriteLine("=== Remote CMD Relay Server ===");
Console.WriteLine($"Listening on: {protocol}://0.0.0.0:7890");
Console.WriteLine($"Token: {token}");
Console.WriteLine($"TLS: {(noTls ? "disabled" : "enabled (self-signed)")}");
Console.WriteLine($"Encryption: AES-256-GCM (always on)");
Console.WriteLine();
Console.WriteLine("Client setup (run on target machine):");
Console.WriteLine($"  RemoteCmd.Client.exe <THIS_SERVER_IP> {token}");
Console.WriteLine();
Console.WriteLine("API (local controller):");
Console.WriteLine($"  curl -X POST {protocol}://localhost:7890/api/exec?token={token} -H \"Content-Type: application/json\" -d \"{{\\\"command\\\":\\\"hostname\\\"}}\"");
Console.WriteLine();

// Auth middleware - token required for /api/ endpoints
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    if (path.StartsWith("/api/"))
    {
        var reqToken = context.Request.Query["token"].FirstOrDefault()
                       ?? context.Request.Headers["X-Token"].FirstOrDefault();
        if (reqToken != token)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Invalid token");
            return;
        }
    }
    await next();
});

// === Command execution (client-facing: encrypted) ===

app.MapGet("/api/poll", () =>
{
    lastClientPoll = DateTime.UtcNow;
    if (pendingCommand != null)
    {
        var cmd = pendingCommand;
        pendingCommand = null;
        // Encrypt command for client
        return Results.Ok(new { command = Crypto.EncryptString(cmd) });
    }
    return Results.Ok(new { command = (string?)null });
});

app.MapPost("/api/result", async (HttpRequest req) =>
{
    // Client sends AES-encrypted result bytes
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    var decryptedBytes = Crypto.Decrypt(ms.ToArray());
    var result = JsonSerializer.Deserialize<CommandResult>(decryptedBytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (result != null && resultTcs != null)
        resultTcs.TrySetResult(result);
    return Results.Ok();
});

// === Command execution (controller-facing: plaintext, localhost) ===

app.MapPost("/api/exec", async (HttpRequest req) =>
{
    var body = await req.ReadFromJsonAsync<CommandRequest>();
    if (body?.Command == null)
        return Results.BadRequest(new { error = "Missing command" });

    var isConnected = (DateTime.UtcNow - lastClientPoll).TotalSeconds < 10;
    if (!isConnected)
        return Results.Ok(new CommandResult { Output = "[ERROR] No client connected", ExitCode = -1 });

    if (!await commandLock.WaitAsync(TimeSpan.FromSeconds(2)))
        return Results.Ok(new CommandResult { Output = "[ERROR] Another command is pending", ExitCode = -1 });

    try
    {
        resultTcs = new TaskCompletionSource<CommandResult>();
        pendingCommand = body.Command;

        var timeout = body.TimeoutSeconds > 0 ? body.TimeoutSeconds : 30;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));

        try
        {
            var result = await resultTcs.Task.WaitAsync(cts.Token);
            return Results.Ok(result);
        }
        catch (OperationCanceledException)
        {
            pendingCommand = null;
            return Results.Ok(new CommandResult { Output = $"[TIMEOUT] No response after {timeout}s", ExitCode = -1 });
        }
    }
    finally
    {
        resultTcs = null;
        commandLock.Release();
    }
});

// === File transfer: Upload (local → remote, encrypted) ===

app.MapPost("/api/upload", async (HttpRequest req) =>
{
    var remotePath = req.Query["path"].FirstOrDefault();
    if (string.IsNullOrEmpty(remotePath))
        return Results.BadRequest(new { error = "Missing ?path= parameter" });

    var isConnected = (DateTime.UtcNow - lastClientPoll).TotalSeconds < 10;
    if (!isConnected)
        return Results.BadRequest(new { error = "No client connected" });

    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    var data = ms.ToArray();

    Console.WriteLine($"[UPLOAD] {data.Length / 1024 / 1024}MB → {remotePath}");

    pendingUpload = new FileTransfer { Path = remotePath, Data = data };
    uploadTcs = new TaskCompletionSource<bool>();

    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    try
    {
        var ok = await uploadTcs.Task.WaitAsync(cts.Token);
        return ok ? Results.Ok(new { status = "ok", size = data.Length }) : Results.StatusCode(500);
    }
    catch (OperationCanceledException)
    {
        pendingUpload = null;
        return Results.Ok(new { error = "Upload timeout" });
    }
});

// Client polls for pending file transfers (encrypted metadata)
app.MapGet("/api/file-poll", () =>
{
    lastClientPoll = DateTime.UtcNow;

    if (pendingUpload != null)
    {
        var meta = JsonSerializer.Serialize(new { action = "upload", path = pendingUpload.Path, size = pendingUpload.Data!.Length });
        return Results.Ok(new { e = Crypto.EncryptString(meta) });
    }

    if (downloadTcs != null)
    {
        var meta = JsonSerializer.Serialize(new { action = "download", path = pendingDownload?.Path ?? "", size = 0 });
        return Results.Ok(new { e = Crypto.EncryptString(meta) });
    }

    return Results.Ok(new { e = (string?)null });
});

// Client downloads file data for upload-to-remote (encrypted bytes)
app.MapGet("/api/file-data", () =>
{
    if (pendingUpload?.Data == null)
        return Results.NotFound();

    var encrypted = Crypto.Encrypt(pendingUpload.Data);
    return Results.File(encrypted, "application/octet-stream");
});

// Client confirms upload complete
app.MapPost("/api/file-done", () =>
{
    pendingUpload = null;
    uploadTcs?.TrySetResult(true);
    return Results.Ok();
});

// === File transfer: Download (remote → local, encrypted) ===

app.MapGet("/api/download", async (HttpRequest req) =>
{
    var remotePath = req.Query["path"].FirstOrDefault();
    if (string.IsNullOrEmpty(remotePath))
        return Results.BadRequest(new { error = "Missing ?path= parameter" });

    var isConnected = (DateTime.UtcNow - lastClientPoll).TotalSeconds < 10;
    if (!isConnected)
        return Results.BadRequest(new { error = "No client connected" });

    Console.WriteLine($"[DOWNLOAD] ← {remotePath}");

    pendingDownload = new FileTransfer { Path = remotePath };
    downloadTcs = new TaskCompletionSource<FileTransfer>();

    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    try
    {
        var result = await downloadTcs.Task.WaitAsync(cts.Token);
        if (result.Data == null)
            return Results.NotFound(new { error = result.Error ?? "File not found" });
        return Results.File(result.Data, "application/octet-stream", Path.GetFileName(remotePath));
    }
    catch (OperationCanceledException)
    {
        pendingDownload = null;
        downloadTcs = null;
        return Results.StatusCode(504);
    }
});

// Client uploads file data for download-from-remote (encrypted bytes)
app.MapPost("/api/file-upload", async (HttpRequest req) =>
{
    var error = req.Query["error"].FirstOrDefault();
    if (!string.IsNullOrEmpty(error))
    {
        downloadTcs?.TrySetResult(new FileTransfer { Error = error });
        pendingDownload = null;
        downloadTcs = null;
        return Results.Ok();
    }

    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    // Decrypt file data from client
    var decrypted = Crypto.Decrypt(ms.ToArray());
    downloadTcs?.TrySetResult(new FileTransfer { Data = decrypted });
    pendingDownload = null;
    downloadTcs = null;
    return Results.Ok();
});

// === Status (plaintext) ===

app.MapGet("/api/status", () =>
{
    var connected = (DateTime.UtcNow - lastClientPoll).TotalSeconds < 10;
    return Results.Ok(new
    {
        clientConnected = connected,
        lastPoll = lastClientPoll,
        secondsAgo = connected ? (int)(DateTime.UtcNow - lastClientPoll).TotalSeconds : -1,
        encryption = "AES-256-GCM",
        tls = !noTls
    });
});

app.MapGet("/", () => Results.Text(
    "Remote CMD Relay Server v1.0.0\n" +
    $"Encryption: AES-256-GCM | TLS: {(noTls ? "off" : "self-signed")}\n\n" +
    "GET  /api/status                    - Check client\n" +
    "POST /api/exec                      - Run command {\"command\":\"...\", \"timeoutSeconds\":30}\n" +
    "POST /api/upload?path=C:\\dest\\f.zip  - Upload file to remote (--data-binary @local.zip)\n" +
    "GET  /api/download?path=C:\\src\\f.zip - Download file from remote\n" +
    "All endpoints need ?token=<TOKEN>", "text/plain"));

app.Run();

// === Self-signed certificate generation ===

static X509Certificate2 GenerateSelfSignedCert()
{
    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest(
        "CN=RemoteCmd, O=NKS Hub",
        rsa,
        HashAlgorithmName.SHA256,
        RSASignaturePadding.Pkcs1);

    request.CertificateExtensions.Add(
        new X509BasicConstraintsExtension(false, false, 0, false));
    request.CertificateExtensions.Add(
        new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));

    // SAN: localhost + wildcard IPs
    var sanBuilder = new SubjectAlternativeNameBuilder();
    sanBuilder.AddDnsName("localhost");
    sanBuilder.AddDnsName("*");
    sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);
    sanBuilder.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
    request.CertificateExtensions.Add(sanBuilder.Build());

    var cert = request.CreateSelfSigned(
        DateTimeOffset.UtcNow.AddDays(-1),
        DateTimeOffset.UtcNow.AddYears(5));

    return cert;
}

// === Models ===

record CommandRequest(string Command, int TimeoutSeconds = 30);

record CommandResult
{
    public string Output { get; set; } = "";
    public int ExitCode { get; set; }
}

class FileTransfer
{
    public string? Path { get; set; }
    public byte[]? Data { get; set; }
    public string? Error { get; set; }
}
