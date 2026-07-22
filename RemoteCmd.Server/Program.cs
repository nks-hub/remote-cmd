using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteCmd.Shared;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 200_000_000);

var token = args.Length > 0 && !args[0].StartsWith("-")
    ? args[0]
    : Environment.GetEnvironmentVariable("REMOTECMD_TOKEN")
      ?? Guid.NewGuid().ToString("N")[..16];
var noTls = args.Contains("--no-tls")
    || string.Equals(Environment.GetEnvironmentVariable("REMOTECMD_NO_TLS"), "1", StringComparison.Ordinal)
    || string.Equals(Environment.GetEnvironmentVariable("REMOTECMD_NO_TLS"), "true", StringComparison.OrdinalIgnoreCase);
var dashboard = args.Contains("--dashboard")
    || string.Equals(Environment.GetEnvironmentVariable("REMOTECMD_DASHBOARD"), "1", StringComparison.Ordinal);

const int MinTokenLength = 12;
var allowShortToken = string.Equals(Environment.GetEnvironmentVariable("REMOTECMD_ALLOW_SHORT_TOKEN"), "1", StringComparison.Ordinal);
if (token.Length < MinTokenLength && !allowShortToken)
{
    Console.Error.WriteLine($"[FATAL] Token too short ({token.Length} chars). Minimum is {MinTokenLength}. Set REMOTECMD_ALLOW_SHORT_TOKEN=1 to bypass (NOT RECOMMENDED).");
    return 2;
}

// Session GC: remove clients that have not polled for longer than this threshold.
var staleAfter = TimeSpan.FromHours(1);

// Listen port: default 7890, overridable via REMOTECMD_PORT (CI / multi-instance).
var port = int.TryParse(Environment.GetEnvironmentVariable("REMOTECMD_PORT"), out var p) && p is > 0 and < 65536
    ? p
    : 7890;

if (noTls)
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}
else
{
    var cert = GenerateSelfSignedCert();
    var certPath = Path.Combine(AppContext.BaseDirectory, "remotecmd.pfx");
    var certPassword = Guid.NewGuid().ToString("N")[..16];
    File.WriteAllBytes(certPath, cert.Export(X509ContentType.Pfx, certPassword));

    builder.WebHost.UseUrls($"https://0.0.0.0:{port}");
    builder.WebHost.ConfigureKestrel(o =>
    {
        o.Limits.MaxRequestBodySize = 200_000_000;
        o.ConfigureHttpsDefaults(https =>
        {
            https.ServerCertificate = X509CertificateLoader.LoadPkcs12FromFile(certPath, certPassword);
        });
    });
}

// The live dashboard owns the console, so silence framework request/lifetime chatter that would
// otherwise scroll through it. Explicit relay events (UPLOAD/DOWNLOAD/GC) still print.
if (dashboard)
    builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

var app = builder.Build();

Crypto.Init(token);

var startedUtc = DateTime.UtcNow;
var clients = new ConcurrentDictionary<string, ClientSession>();

var protocol = noTls ? "http" : "https";
Console.WriteLine("=== Remote CMD Relay Server ===");
Console.WriteLine($"Listening on: {protocol}://0.0.0.0:{port}");
Console.WriteLine($"Token: {token}");
Console.WriteLine($"TLS: {(noTls ? "disabled" : "enabled (self-signed)")}");
Console.WriteLine($"Encryption: AES-256-GCM (always on)");
Console.WriteLine($"Multi-client: enabled");
Console.WriteLine($"Session GC threshold: {staleAfter.TotalMinutes:N0} minutes");
Console.WriteLine($"Live dashboard: {(dashboard ? "on (--dashboard)" : "off (add --dashboard)")}");
Console.WriteLine();
Console.WriteLine("Client setup (run on target machine):");
Console.WriteLine($"  RemoteCmd.Client.exe <THIS_SERVER_IP> {token}");
Console.WriteLine();

// Session GC loop (every 5 minutes)
var gcCts = app.Lifetime.ApplicationStopping;
_ = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
    while (!gcCts.IsCancellationRequested)
    {
        try
        {
            await timer.WaitForNextTickAsync(gcCts);
            var pruned = ClientRegistry.PruneStale(clients, staleAfter, DateTime.UtcNow);
            if (pruned > 0) Console.WriteLine($"[GC] pruned {pruned} stale client session(s)");
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex) { Console.Error.WriteLine($"[GC ERROR] {ex.Message}"); }
    }
});

// Optional live status dashboard: a top-style fixed view repainted every second showing who's
// connected, for how long, how many commands are running/queued, and file-transfer state.
if (dashboard)
{
    Dashboard.EnableAnsi();
    _ = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!gcCts.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(gcCts);
                Console.Out.Write(Dashboard.Render(clients, port, noTls, startedUtc, DateTime.UtcNow));
                Console.Out.Flush();
            }
            catch (OperationCanceledException) { break; }
            catch { /* never let the status view take the server down */ }
        }
    });
}

// Auth middleware
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    if (path.StartsWith("/api/"))
    {
        var reqToken = context.Request.Query["token"].FirstOrDefault()
                       ?? context.Request.Headers["X-Token"].FirstOrDefault()
                       ?? ExtractBearer(context.Request.Headers["Authorization"].FirstOrDefault());
        if (!TokensEqual(reqToken, token))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Invalid token");
            return;
        }
    }
    await next();
});

// === Client-facing: polling (encrypted) ===

app.MapGet("/api/poll", (HttpRequest req) =>
{
    var session = TouchSession(req, clients);
    // Hand out one queued command per poll. Skip any that timed out before being delivered.
    while (session.CommandQueue.TryDequeue(out var pending))
    {
        if (pending.Cancelled) continue;
        return Results.Ok(new { command = Crypto.EncryptString(pending.Command), requestId = pending.RequestId });
    }
    return Results.Ok(new { command = (string?)null });
});

app.MapPost("/api/result", async (HttpRequest req) =>
{
    var session = TouchSession(req, clients);
    var requestId = req.Query["requestId"].FirstOrDefault();
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    CommandResult result;
    try
    {
        var decryptedBytes = Crypto.Decrypt(ms.ToArray());
        result = JsonSerializer.Deserialize<CommandResult>(decryptedBytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                 ?? new CommandResult { Output = "[EMPTY RESULT]", ExitCode = -1 };
    }
    catch (Exception ex)
    {
        result = new CommandResult { Output = $"[DECRYPT ERROR] {ex.Message}", ExitCode = -1 };
    }
    CompleteCommand(session, requestId, result);
    return Results.Ok();
});

app.MapGet("/api/file-poll", (HttpRequest req) =>
{
    var session = TouchSession(req, clients);

    if (session.PendingUpload != null)
    {
        var meta = JsonSerializer.Serialize(new { action = "upload", path = session.PendingUpload.Path, size = session.PendingUpload.Data!.Length });
        return Results.Ok(new { e = Crypto.EncryptString(meta) });
    }
    if (session.PendingDownload != null)
    {
        var meta = JsonSerializer.Serialize(new { action = "download", path = session.PendingDownload.Path ?? "", size = 0 });
        return Results.Ok(new { e = Crypto.EncryptString(meta) });
    }
    return Results.Ok(new { e = (string?)null });
});

app.MapGet("/api/file-data", (HttpRequest req) =>
{
    var session = TouchSession(req, clients);
    if (session.PendingUpload?.Data == null) return Results.NotFound();
    var encrypted = Crypto.Encrypt(session.PendingUpload.Data);
    return Results.File(encrypted, "application/octet-stream");
});

app.MapPost("/api/file-done", (HttpRequest req) =>
{
    var session = TouchSession(req, clients);
    session.PendingUpload = null;
    session.UploadTcs?.TrySetResult(true);
    return Results.Ok();
});

app.MapPost("/api/file-upload", async (HttpRequest req) =>
{
    var session = TouchSession(req, clients);
    var error = req.Query["error"].FirstOrDefault();
    if (!string.IsNullOrEmpty(error))
    {
        session.DownloadTcs?.TrySetResult(new FileTransfer { Error = error });
        session.PendingDownload = null;
        session.DownloadTcs = null;
        return Results.Ok();
    }

    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    try
    {
        var decrypted = Crypto.Decrypt(ms.ToArray());
        session.DownloadTcs?.TrySetResult(new FileTransfer { Data = decrypted });
    }
    catch (Exception ex)
    {
        session.DownloadTcs?.TrySetResult(new FileTransfer { Error = $"[DECRYPT ERROR] {ex.Message}" });
    }
    session.PendingDownload = null;
    session.DownloadTcs = null;
    return Results.Ok();
});

// === Controller-facing: commands, file transfer ===

app.MapPost("/api/exec", async (HttpRequest req) =>
{
    CommandRequest? body;
    try { body = await req.ReadFromJsonAsync<CommandRequest>(); }
    catch { return Results.BadRequest(new { error = "Invalid JSON body" }); }

    if (body?.Command == null)
        return Results.BadRequest(new { error = "Missing command" });

    var target = ResolveTarget(req, clients);
    if (target.Error != null)
        return Results.Ok(new CommandResult { Output = $"[ERROR] {target.Error}", ExitCode = -1 });

    var session = target.Session!;

    // Each exec gets its own correlated slot. Multiple execs (same or different sessions) run
    // concurrently — the client picks them up one per poll and returns each result by requestId.
    var pending = new PendingCommand { Command = body.Command };
    session.InFlight[pending.RequestId] = pending;
    session.CommandQueue.Enqueue(pending);

    var timeout = body.TimeoutSeconds > 0 ? Math.Min(body.TimeoutSeconds, 300) : 30;
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
    try
    {
        var result = await pending.Tcs.Task.WaitAsync(cts.Token);
        return Results.Ok(result);
    }
    catch (OperationCanceledException)
    {
        pending.Cancelled = true;
        session.InFlight.TryRemove(pending.RequestId, out _);
        return Results.Ok(new CommandResult { Output = $"[TIMEOUT] No response from '{session.Name}' after {timeout}s", ExitCode = -1 });
    }
});

app.MapPost("/api/upload", async (HttpRequest req) =>
{
    var remotePath = req.Query["path"].FirstOrDefault();
    if (string.IsNullOrEmpty(remotePath))
        return Results.BadRequest(new { error = "Missing ?path= parameter" });

    var target = ResolveTarget(req, clients);
    if (target.Error != null)
        return Results.BadRequest(new { error = target.Error });

    var session = target.Session!;

    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    var data = ms.ToArray();

    Console.WriteLine($"[UPLOAD] {session.Name}: {data.Length / 1024 / 1024}MB -> {remotePath}");

    session.PendingUpload = new FileTransfer { Path = remotePath, Data = data };
    session.UploadTcs = new TaskCompletionSource<bool>();

    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    try
    {
        var ok = await session.UploadTcs.Task.WaitAsync(cts.Token);
        return ok
            ? Results.Ok(new { status = "ok", size = data.Length, client = session.Name })
            : Results.StatusCode(500);
    }
    catch (OperationCanceledException)
    {
        session.PendingUpload = null;
        return Results.Ok(new { error = "Upload timeout" });
    }
});

app.MapGet("/api/download", async (HttpRequest req) =>
{
    var remotePath = req.Query["path"].FirstOrDefault();
    if (string.IsNullOrEmpty(remotePath))
        return Results.BadRequest(new { error = "Missing ?path= parameter" });

    var target = ResolveTarget(req, clients);
    if (target.Error != null)
        return Results.BadRequest(new { error = target.Error });

    var session = target.Session!;

    Console.WriteLine($"[DOWNLOAD] {session.Name}: <- {remotePath}");

    session.PendingDownload = new FileTransfer { Path = remotePath };
    session.DownloadTcs = new TaskCompletionSource<FileTransfer>();

    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    try
    {
        var result = await session.DownloadTcs.Task.WaitAsync(cts.Token);
        if (result.Data == null)
            return Results.NotFound(new { error = result.Error ?? "File not found" });
        return Results.File(result.Data, "application/octet-stream", Path.GetFileName(remotePath));
    }
    catch (OperationCanceledException)
    {
        session.PendingDownload = null;
        session.DownloadTcs = null;
        return Results.StatusCode(504);
    }
});

// === Info endpoints ===

app.MapGet("/api/clients", () =>
{
    var list = clients.Values
        .OrderBy(c => c.Name)
        .Select(c => new
        {
            id = c.Id,
            name = c.Name,
            lastPoll = c.LastPoll,
            secondsAgo = c.LastPoll == DateTime.MinValue ? -1 : (int)(DateTime.UtcNow - c.LastPoll).TotalSeconds,
            connected = c.IsConnected(),
            running = c.InFlight.Count,
            queued = c.CommandQueue.Count,
            served = Interlocked.Read(ref c.CommandsServed),
            connectedForSeconds = (int)(DateTime.UtcNow - c.ConnectedSince).TotalSeconds
        }).ToList();
    return Results.Ok(new { count = list.Count, connected = list.Count(x => x.connected), clients = list });
});

app.MapGet("/api/status", (HttpRequest req) =>
{
    var clientParam = req.Query["client"].FirstOrDefault();
    if (!string.IsNullOrEmpty(clientParam))
    {
        var s = FindByNameOrId(clients, clientParam);
        if (s == null) return Results.NotFound(new { error = $"Unknown client '{clientParam}'" });
        return Results.Ok(new
        {
            clientConnected = s.IsConnected(),
            name = s.Name,
            id = s.Id,
            lastPoll = s.LastPoll,
            secondsAgo = s.LastPoll == DateTime.MinValue ? -1 : (int)(DateTime.UtcNow - s.LastPoll).TotalSeconds,
            encryption = "AES-256-GCM",
            tls = !noTls
        });
    }

    var connectedCount = clients.Values.Count(c => c.IsConnected());
    return Results.Ok(new
    {
        clientConnected = connectedCount > 0,
        totalClients = clients.Count,
        connectedClients = connectedCount,
        encryption = "AES-256-GCM",
        tls = !noTls
    });
});

app.MapGet("/", () => Results.Text(
    "Remote CMD Relay Server v1.1.0 (multi-client)\n" +
    $"Encryption: AES-256-GCM | TLS: {(noTls ? "off" : "self-signed")}\n" +
    $"Connected clients: {clients.Values.Count(c => c.IsConnected())}/{clients.Count}\n\n" +
    "GET  /api/status[?client=X]                - Check client(s)\n" +
    "GET  /api/clients                          - List all clients\n" +
    "POST /api/exec[?client=X]                  - Run command {\"command\":\"...\"}\n" +
    "POST /api/upload?path=...[&client=X]       - Upload file (binary body)\n" +
    "GET  /api/download?path=...[&client=X]     - Download file\n" +
    "All endpoints need token (query/header/Bearer).", "text/plain"));

app.Run();
return 0;

// === Helpers ===

static string? ExtractBearer(string? authHeader)
{
    if (string.IsNullOrEmpty(authHeader)) return null;
    const string prefix = "Bearer ";
    return authHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        ? authHeader[prefix.Length..].Trim()
        : null;
}

static bool TokensEqual(string? a, string b)
{
    if (a == null || a.Length != b.Length) return false;
    var aBytes = System.Text.Encoding.UTF8.GetBytes(a);
    var bBytes = System.Text.Encoding.UTF8.GetBytes(b);
    return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
}

static ClientSession TouchSession(HttpRequest req, ConcurrentDictionary<string, ClientSession> clients)
{
    var clientId = req.Query["clientId"].FirstOrDefault();
    var name = req.Query["name"].FirstOrDefault();

    if (string.IsNullOrEmpty(clientId))
    {
        // Legacy client without clientId: synthesize stable id from remote IP
        var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        clientId = "legacy-" + ip;
        name ??= ip;
    }
    name ??= clientId;

    var session = clients.GetOrAdd(clientId, id => new ClientSession { Id = id, Name = name });
    if (!string.Equals(session.Name, name, StringComparison.Ordinal))
    {
        // Two processes polling with the same clientId but different --name → name flapping.
        // Throttle the warning so we don't spam logs (every 10s per session is plenty).
        if (DateTime.UtcNow - session.LastNameFlapWarning > TimeSpan.FromSeconds(10))
        {
            Console.Error.WriteLine($"[WARN] clientId {clientId} switched name '{session.Name}' -> '{name}' — likely two processes sharing the same client.id file");
            session.LastNameFlapWarning = DateTime.UtcNow;
        }
        session.Name = name;
    }
    session.LastPoll = DateTime.UtcNow;
    return session;
}

static ClientSession? FindByNameOrId(ConcurrentDictionary<string, ClientSession> clients, string nameOrId)
{
    if (clients.TryGetValue(nameOrId, out var byId)) return byId;
    return clients.Values.FirstOrDefault(c =>
        string.Equals(c.Name, nameOrId, StringComparison.OrdinalIgnoreCase));
}

// Match an incoming result to the waiting exec. New clients echo the requestId for exact routing;
// legacy clients omit it, so we complete the oldest in-flight command (they only run one at a time).
static void CompleteCommand(ClientSession session, string? requestId, CommandResult result)
{
    PendingCommand? pending = null;
    if (!string.IsNullOrEmpty(requestId))
        session.InFlight.TryRemove(requestId, out pending);

    if (pending == null)
    {
        var oldest = session.InFlight.Values
            .Where(p => !p.Cancelled)
            .OrderBy(p => p.Seq)
            .FirstOrDefault();
        if (oldest != null)
            session.InFlight.TryRemove(oldest.RequestId, out pending);
    }

    if (pending == null) return;
    Interlocked.Increment(ref session.CommandsServed);
    pending.Tcs.TrySetResult(result);
}

static TargetResolution ResolveTarget(HttpRequest req, ConcurrentDictionary<string, ClientSession> clients)
{
    var clientParam = req.Query["client"].FirstOrDefault();

    if (!string.IsNullOrEmpty(clientParam))
    {
        var s = FindByNameOrId(clients, clientParam);
        if (s == null)
        {
            var available = string.Join(", ", clients.Values
                .Where(c => c.IsConnected())
                .Select(c => $"{c.Name} ({c.Id[..Math.Min(8, c.Id.Length)]})"));
            var hint = string.IsNullOrEmpty(available) ? "no clients connected" : $"connected: {available}";
            return new TargetResolution { Error = $"Unknown client '{clientParam}' — {hint}" };
        }
        if (!s.IsConnected()) return new TargetResolution { Error = $"Client '{s.Name}' not connected" };
        return new TargetResolution { Session = s };
    }

    var connected = clients.Values.Where(c => c.IsConnected()).ToList();
    if (connected.Count == 0) return new TargetResolution { Error = "No client connected" };
    if (connected.Count == 1) return new TargetResolution { Session = connected[0] };

    var names = string.Join(", ", connected.Select(c => c.Name));
    return new TargetResolution { Error = $"Multiple clients connected ({names}); specify ?client=<name|id>" };
}

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
    sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);
    sanBuilder.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
    request.CertificateExtensions.Add(sanBuilder.Build());

    return request.CreateSelfSigned(
        DateTimeOffset.UtcNow.AddDays(-1),
        DateTimeOffset.UtcNow.AddYears(5));
}

// === Models ===

public record CommandRequest(string Command, int TimeoutSeconds = 30);

public record CommandResult
{
    public string Output { get; set; } = "";
    public int ExitCode { get; set; }
}

public class FileTransfer
{
    public string? Path { get; set; }
    public byte[]? Data { get; set; }
    public string? Error { get; set; }
}

public class ClientSession
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime LastPoll { get; set; } = DateTime.MinValue;
    public DateTime LastNameFlapWarning { get; set; } = DateTime.MinValue;
    public DateTime ConnectedSince { get; set; } = DateTime.UtcNow;
    public long CommandsServed;   // total results delivered; mutable field for Interlocked

    // Commands queued for the next poll(s), and those handed out awaiting a result (keyed by id).
    public ConcurrentQueue<PendingCommand> CommandQueue { get; } = new();
    public ConcurrentDictionary<string, PendingCommand> InFlight { get; } = new();

    public FileTransfer? PendingUpload { get; set; }
    public TaskCompletionSource<bool>? UploadTcs { get; set; }
    public FileTransfer? PendingDownload { get; set; }
    public TaskCompletionSource<FileTransfer>? DownloadTcs { get; set; }

    public bool IsConnected() => (DateTime.UtcNow - LastPoll).TotalSeconds < 10;
}

/// <summary>
/// One in-flight command with its own result correlation. Many can be pending on a single session
/// at once; each carries a unique <see cref="RequestId"/> so the matching /api/exec caller unblocks
/// when its result returns. <see cref="Seq"/> gives a total order for the legacy (no-requestId) path.
/// </summary>
public sealed class PendingCommand
{
    private static long _seqCounter;

    public string RequestId { get; } = Guid.NewGuid().ToString("N");
    public required string Command { get; init; }
    public TaskCompletionSource<CommandResult> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public long Seq { get; } = Interlocked.Increment(ref _seqCounter);
    public volatile bool Cancelled;
}

public class TargetResolution
{
    public ClientSession? Session { get; set; }
    public string? Error { get; set; }
}

public partial class Program { }
