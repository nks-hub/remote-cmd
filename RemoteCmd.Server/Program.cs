using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 200_000_000); // 200MB

builder.Services.AddLogging();

// Rate limiting: 60 req/min general, 5 auth failures/5min
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("general", o =>
    {
        o.PermitLimit = 60;
        o.Window = TimeSpan.FromMinutes(1);
        o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        o.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("auth-failures", o =>
    {
        o.PermitLimit = 5;
        o.Window = TimeSpan.FromMinutes(5);
        o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        o.QueueLimit = 0;
    });
    options.RejectionStatusCode = 429;
});

// Parse arguments: <token> [--no-tls] [--bind <addr>] [--show-token]
// Environment variables REMOTECMD_TOKEN and REMOTECMD_NO_TLS are used as fallback (e.g. for tests).
var token = args.Length > 0 && !args[0].StartsWith('-')
    ? args[0]
    : Environment.GetEnvironmentVariable("REMOTECMD_TOKEN") ?? Guid.NewGuid().ToString("N")[..12];
var noTls = args.Contains("--no-tls") || Environment.GetEnvironmentVariable("REMOTECMD_NO_TLS") == "1";
var showToken = args.Contains("--show-token");

var bindIndex = Array.IndexOf(args, "--bind");
var bindAddress = bindIndex >= 0 && bindIndex + 1 < args.Length ? args[bindIndex + 1] : "127.0.0.1";

var port = 7890;
var scheme = noTls ? "http" : "https";
var listenUrl = $"{scheme}://{bindAddress}:{port}";

if (noTls)
{
    builder.WebHost.UseUrls(listenUrl);
}
else
{
    // Generate self-signed certificate and load directly into memory (no disk write)
    var cert = GenerateSelfSignedCert();

    builder.WebHost.UseUrls(listenUrl);
    builder.WebHost.ConfigureKestrel(o =>
    {
        o.Limits.MaxRequestBodySize = 200_000_000;
        o.ConfigureHttpsDefaults(https =>
        {
            https.ServerCertificate = cert;
        });
    });
}

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILogger<Program>>();

app.UseRateLimiter();

// Initialize AES-256-GCM encryption from token
Crypto.Init(token);

// Thread-safe relay state
var relay = new RelayState();
var commandLock = new SemaphoreSlim(1, 1);

// Startup info - token masked unless --show-token
var tokenDisplay = showToken ? token : token[..Math.Min(5, token.Length)] + "****";
Console.WriteLine("=== Remote CMD Relay Server ===");
Console.WriteLine($"Listening on: {listenUrl}");
Console.WriteLine($"Token: {tokenDisplay}");
if (!showToken)
    Console.WriteLine("(Use --show-token to display full token)");
Console.WriteLine($"TLS: {(noTls ? "disabled" : "enabled (self-signed)")}");
Console.WriteLine($"Encryption: AES-256-GCM (always on)");
Console.WriteLine($"Bind address: {bindAddress}");
Console.WriteLine();
Console.WriteLine("Client setup (run on target machine):");
Console.WriteLine("  RemoteCmd.Client.exe <THIS_SERVER_IP> <TOKEN>");
Console.WriteLine();

// Auth middleware - Bearer token required for /api/ endpoints
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    if (path.StartsWith("/api/"))
    {
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        string? reqToken = null;
        if (authHeader != null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            reqToken = authHeader["Bearer ".Length..].Trim();

        var tokenBytes = Encoding.UTF8.GetBytes(token);
        var reqBytes = reqToken != null ? Encoding.UTF8.GetBytes(reqToken) : Array.Empty<byte>();

        // Constant-time comparison to prevent timing attacks
        var isValid = reqToken != null
            && tokenBytes.Length == reqBytes.Length
            && CryptographicOperations.FixedTimeEquals(tokenBytes, reqBytes);

        if (!isValid)
        {
            logger.LogWarning("Auth failure from {RemoteIp} for {Path}", context.Connection.RemoteIpAddress, path);
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }
    }
    await next();
});

// === Command execution (client-facing: encrypted) ===

app.MapGet("/api/poll", () =>
{
    relay.UpdateLastPoll();
    var cmd = relay.TakeCommand();
    if (cmd != null)
    {
        logger.LogDebug("Poll: dispatching command to client");
        return Results.Ok(new { command = Crypto.EncryptString(cmd) });
    }
    return Results.Ok(new { command = (string?)null });
}).RequireRateLimiting("general");

app.MapPost("/api/result", async (HttpRequest req) =>
{
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    var decryptedBytes = Crypto.Decrypt(ms.ToArray());
    var result = JsonSerializer.Deserialize<CommandResult>(decryptedBytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (result != null)
    {
        relay.SetResult(result);
        logger.LogInformation("Command result received: ExitCode={ExitCode}", result.ExitCode);
    }
    return Results.Ok();
}).RequireRateLimiting("general");

// === Command execution (controller-facing: plaintext, auth-protected) ===

app.MapPost("/api/exec", async (HttpRequest req) =>
{
    CommandRequest? body;
    try
    {
        body = await req.ReadFromJsonAsync<CommandRequest>();
    }
    catch (System.Text.Json.JsonException)
    {
        return Results.BadRequest(new { error = "Invalid JSON body" });
    }
    if (body?.Command == null)
        return Results.BadRequest(new { error = "Missing command" });

    if (!relay.IsClientConnected)
        return Results.Ok(new CommandResult { Output = "[ERROR] No client connected", ExitCode = -1 });

    if (!await commandLock.WaitAsync(TimeSpan.FromSeconds(2)))
        return Results.Ok(new CommandResult { Output = "[ERROR] Another command is pending", ExitCode = -1 });

    logger.LogInformation("Exec: {Command}", body.Command);

    try
    {
        var tcs = relay.TrySetCommand(body.Command);
        if (tcs == null)
            return Results.Ok(new CommandResult { Output = "[ERROR] State conflict", ExitCode = -1 });

        var timeout = body.TimeoutSeconds > 0 ? body.TimeoutSeconds : 30;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));

        try
        {
            var result = await tcs.Task.WaitAsync(cts.Token);
            return Results.Ok(result);
        }
        catch (OperationCanceledException)
        {
            relay.CancelCommand();
            logger.LogWarning("Exec timeout after {Timeout}s for: {Command}", timeout, body.Command);
            return Results.Ok(new CommandResult { Output = $"[TIMEOUT] No response after {timeout}s", ExitCode = -1 });
        }
    }
    finally
    {
        commandLock.Release();
    }
}).RequireRateLimiting("general");

// === File transfer: Upload (local → remote, encrypted) ===

app.MapPost("/api/upload", async (HttpRequest req) =>
{
    var remotePath = req.Query["path"].FirstOrDefault();
    if (string.IsNullOrEmpty(remotePath))
        return Results.BadRequest(new { error = "Missing ?path= parameter" });

    if (!relay.IsClientConnected)
        return Results.BadRequest(new { error = "No client connected" });

    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    var data = ms.ToArray();

    logger.LogInformation("Upload: {SizeMB}MB → {RemotePath}", data.Length / 1024 / 1024, remotePath);

    var tcs = relay.TrySetUpload(new FileTransfer { Path = remotePath, Data = data });
    if (tcs == null)
        return Results.StatusCode(409); // Conflict - upload already pending

    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    try
    {
        var ok = await tcs.Task.WaitAsync(cts.Token);
        return ok ? Results.Ok(new { status = "ok", size = data.Length }) : Results.StatusCode(500);
    }
    catch (OperationCanceledException)
    {
        relay.CancelUpload();
        logger.LogWarning("Upload timeout for {RemotePath}", remotePath);
        return Results.Json(new { error = "Upload timeout" }, statusCode: 504);
    }
}).RequireRateLimiting("general");

// Client polls for pending file transfers (encrypted metadata)
app.MapGet("/api/file-poll", () =>
{
    relay.UpdateLastPoll();

    var upload = relay.PeekUpload();
    if (upload != null)
    {
        var meta = JsonSerializer.Serialize(new { action = "upload", path = upload.Path, size = upload.Data!.Length });
        return Results.Ok(new { e = Crypto.EncryptString(meta) });
    }

    if (relay.HasPendingDownload)
    {
        var dl = relay.PeekDownload();
        var meta = JsonSerializer.Serialize(new { action = "download", path = dl?.Path ?? "", size = 0 });
        return Results.Ok(new { e = Crypto.EncryptString(meta) });
    }

    return Results.Ok(new { e = (string?)null });
}).RequireRateLimiting("general");

// Client downloads file data for upload-to-remote (encrypted bytes)
app.MapGet("/api/file-data", () =>
{
    var upload = relay.PeekUpload();
    if (upload?.Data == null)
        return Results.NotFound();

    var encrypted = Crypto.Encrypt(upload.Data);
    return Results.File(encrypted, "application/octet-stream");
}).RequireRateLimiting("general");

// Client confirms upload complete
app.MapPost("/api/file-done", () =>
{
    relay.CompleteUpload();
    logger.LogInformation("Upload completed by client");
    return Results.Ok();
}).RequireRateLimiting("general");

// === File transfer: Download (remote → local, encrypted) ===

app.MapGet("/api/download", async (HttpRequest req) =>
{
    var remotePath = req.Query["path"].FirstOrDefault();
    if (string.IsNullOrEmpty(remotePath))
        return Results.BadRequest(new { error = "Missing ?path= parameter" });

    if (!relay.IsClientConnected)
        return Results.BadRequest(new { error = "No client connected" });

    logger.LogInformation("Download: ← {RemotePath}", remotePath);

    var tcs = relay.TrySetDownload(new FileTransfer { Path = remotePath });
    if (tcs == null)
        return Results.StatusCode(409); // Conflict - download already pending

    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    try
    {
        var result = await tcs.Task.WaitAsync(cts.Token);
        if (result.Data == null)
            return Results.NotFound(new { error = result.Error ?? "File not found" });
        return Results.File(result.Data, "application/octet-stream", Path.GetFileName(remotePath));
    }
    catch (OperationCanceledException)
    {
        relay.CancelDownload();
        logger.LogWarning("Download timeout for {RemotePath}", remotePath);
        return Results.StatusCode(504);
    }
}).RequireRateLimiting("general");

// Client uploads file data for download-from-remote (encrypted bytes)
app.MapPost("/api/file-upload", async (HttpRequest req) =>
{
    var error = req.Query["error"].FirstOrDefault();
    if (!string.IsNullOrEmpty(error))
    {
        relay.CompleteDownloadWithError(error);
        return Results.Ok();
    }

    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    var decrypted = Crypto.Decrypt(ms.ToArray());
    relay.CompleteDownload(decrypted);
    logger.LogInformation("Download data received from client: {Size} bytes", decrypted.Length);
    return Results.Ok();
}).RequireRateLimiting("general");

// === Status (plaintext) ===

app.MapGet("/api/status", () =>
{
    return Results.Ok(new
    {
        clientConnected = relay.IsClientConnected,
        lastPoll = relay.LastClientPoll,
        secondsAgo = relay.IsClientConnected ? (int)(DateTime.UtcNow - relay.LastClientPoll).TotalSeconds : -1,
        encryption = "AES-256-GCM",
        tls = !noTls
    });
}).RequireRateLimiting("general");

app.MapGet("/", () => Results.Text(
    "Remote CMD Relay Server v1.1.0\n" +
    $"Encryption: AES-256-GCM | TLS: {(noTls ? "off" : "self-signed")}\n\n" +
    "GET  /api/status                    - Check client\n" +
    "POST /api/exec                      - Run command {\"command\":\"...\", \"timeoutSeconds\":30}\n" +
    "POST /api/upload?path=C:\\dest\\f.zip  - Upload file to remote (--data-binary @local.zip)\n" +
    "GET  /api/download?path=C:\\src\\f.zip - Download file from remote\n" +
    "All endpoints require: Authorization: Bearer <TOKEN>", "text/plain"));

// === Self-signed certificate generation (in-memory, no disk write) ===

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

    var sanBuilder = new SubjectAlternativeNameBuilder();
    sanBuilder.AddDnsName("localhost");
    sanBuilder.AddDnsName("*");
    sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);
    sanBuilder.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
    request.CertificateExtensions.Add(sanBuilder.Build());

    var cert = request.CreateSelfSigned(
        DateTimeOffset.UtcNow.AddDays(-1),
        DateTimeOffset.UtcNow.AddYears(5));

    // Export and reimport to detach from the RSA key (required by Kestrel on some platforms)
    return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
}

app.Run();

public partial class Program { }
