using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http.Features;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteCmd.Shared;

if (args.Length > 0 && args[0] is "--version" or "-v" or "version")
{
    Console.WriteLine($"RemoteCmd.Server {RemoteCmd.Shared.VersionInfo.Version}");
    return 0;
}

// install-service, pair, token, set-password: management verbs, handled before any server starts.
var cli = ServerArgs.Parse(args);
if (cli.Verb != null) return await ServerCli.RunAsync(cli);

// Key under which the auth middleware stashes the credential that matched, for the handlers to pick up.
const string CredentialItemKey = "remotecmd.credential";

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 200_000_000);
// No-ops unless the service manager started the process, so console runs are unaffected.
builder.Services.AddWindowsService(o => o.ServiceName = "RemoteCmdServer");
builder.Services.AddSystemd();

var storePath = cli.ConfigPath ?? ServerStore.DefaultPath();
var store = ServerStore.Open(storePath);
var pairings = new PairingCodes();
var adminGate = new AdminGate();

// Tokens on the command line or in the environment still work, so a relay started the old way keeps
// its clients — but they are accepted for this run only and never written anywhere. Stored
// credentials (pairing, `token add`, `token import`) are the way forward: they carry a role and
// stay off the process list.
var tokens = cli.Tokens.ToList();
if (tokens.Count == 0)
{
    var fromEnv = Environment.GetEnvironmentVariable("REMOTECMD_TOKENS")
                  ?? Environment.GetEnvironmentVariable("REMOTECMD_TOKEN");
    if (!string.IsNullOrWhiteSpace(fromEnv))
        tokens = fromEnv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Distinct(StringComparer.Ordinal).ToList();
}
var noTls = cli.NoTls
    || string.Equals(Environment.GetEnvironmentVariable("REMOTECMD_NO_TLS"), "1", StringComparison.Ordinal)
    || string.Equals(Environment.GetEnvironmentVariable("REMOTECMD_NO_TLS"), "true", StringComparison.OrdinalIgnoreCase);
var dashboard = cli.Dashboard
    || string.Equals(Environment.GetEnvironmentVariable("REMOTECMD_DASHBOARD"), "1", StringComparison.Ordinal);

const int MinTokenLength = 12;
var allowShortToken = string.Equals(Environment.GetEnvironmentVariable("REMOTECMD_ALLOW_SHORT_TOKEN"), "1", StringComparison.Ordinal);
foreach (var t in tokens.Where(t => t.Length < MinTokenLength && !allowShortToken))
{
    Console.Error.WriteLine($"[FATAL] Token too short ({t.Length} chars). Minimum is {MinTokenLength}. Set REMOTECMD_ALLOW_SHORT_TOKEN=1 to bypass (NOT RECOMMENDED).");
    return 2;
}
foreach (var t in tokens) store.AddEphemeral(t);

// Session GC: remove clients that have not polled for longer than this threshold.
var staleAfter = TimeSpan.FromHours(1);

var port = cli.Port ?? EnvPort("REMOTECMD_PORT") ?? 7890;
var adminPort = cli.AdminPort ?? EnvPort("REMOTECMD_ADMIN_PORT") ?? port + 1;

// Two listeners. The relay port faces the network: clients poll it and controllers send commands
// to it, always with a token. The dashboard port is bound to loopback only, so it can be open
// without a password — whoever reaches it is already on this machine.
string? certSha256 = null;
var adminUrl = $"http://localhost:{adminPort}";
if (noTls)
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}", adminUrl);
}
else
{
    var certificate = RelayCertificate.LoadOrCreate(
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(storePath))!, "remotecmd.pfx"), Console.Out);
    certSha256 = certificate.GetCertHashString(HashAlgorithmName.SHA256);

    builder.WebHost.UseUrls($"https://0.0.0.0:{port}", adminUrl);
    builder.WebHost.ConfigureKestrel(o =>
    {
        o.Limits.MaxRequestBodySize = 200_000_000;
        o.ConfigureHttpsDefaults(https => https.ServerCertificate = certificate);
    });
}

// The live dashboard owns the console, so silence framework request/lifetime chatter that would
// otherwise scroll through it. Explicit relay events (UPLOAD/DOWNLOAD/GC) still print.
if (dashboard)
    builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

var app = builder.Build();

var startedUtc = DateTime.UtcNow;
var clients = new ConcurrentDictionary<string, ClientSession>();
var events = new EventLog();
var commands = new CommandLog();
var stats = new RelayStats();
var authThrottle = new AuthThrottle();

var protocol = noTls ? "http" : "https";
Console.WriteLine($"=== Remote CMD Relay Server {RemoteCmd.Shared.VersionInfo.Version} ===");
Console.WriteLine($"Relay:      {protocol}://0.0.0.0:{port}  (clients and controllers, token required)");
Console.WriteLine($"Dashboard:  {adminUrl}  (this machine only{(store.HasPassword ? ", password protected" : "")})");
Console.WriteLine($"Settings:   {storePath}");
Console.WriteLine($"TLS:        {(noTls ? "disabled — tokens travel in clear text" : $"self-signed, SHA-256 {certSha256}")}");
Console.WriteLine($"Accepted:   {store.All().Count(c => !c.Ephemeral)} stored credential(s), {tokens.Count} command-line token(s)");
Console.WriteLine();
Console.WriteLine("Add a machine: open the dashboard and press \"Pair a machine\", or run");
Console.WriteLine("  RemoteCmd.Server pair --name <machine>");
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
            authThrottle.Prune(DateTime.UtcNow);
            var pruned = ClientRegistry.PruneStale(clients, staleAfter, DateTime.UtcNow);
            if (pruned > 0)
            {
                Console.WriteLine($"[GC] pruned {pruned} stale client session(s)");
                events.Add("gc", "-", $"pruned {pruned} stale session(s)");
            }
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
    Dashboard.Enter();
    app.Lifetime.ApplicationStopping.Register(Dashboard.Leave);
    AppDomain.CurrentDomain.ProcessExit += (_, _) => Dashboard.Leave();
    // Restore the terminal on `kill` / service stop too, not just on graceful shutdown.
    System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, _ => Dashboard.Leave());
    System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGINT, _ => Dashboard.Leave());
    _ = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!gcCts.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(gcCts);
                var height = 0;
                try { height = Console.WindowHeight; } catch { /* redirected */ }
                Console.Out.Write(Dashboard.Render(clients, port, noTls, startedUtc, DateTime.UtcNow, events.Lines(), height));
                Console.Out.Flush();
            }
            catch (OperationCanceledException) { break; }
            catch { /* never let the status view take the server down */ }
        }
    });
}

// Endpoints a client machine uses. Everything else on /api is for controllers.
// Routing matches routes without regard to case, so every gate in front of them must do the same:
// a case-sensitive check once let "/API/exec" miss the gate entirely and run commands untokened.
var clientPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "/api/poll", "/api/result", "/api/file-poll", "/api/file-data", "/api/file-done", "/api/file-upload",
};
// Only on the dashboard listener: the pages and the management API.
var dashboardOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "/ui", "/manage", "/login",
};
// The dashboard acts with controller rights; it never stands in for a client.
var dashboardCredential = new Credential
{
    Id = "dashboard", Label = "dashboard", Display = "dashboard", Role = CredentialRole.Controller,
};

app.Use(async (context, next) =>
{
    var path = (context.Request.Path.Value ?? "").TrimEnd('/');
    var now = DateTime.UtcNow;

    if (IsDashboardRequest(context, adminPort))
    {
        if (!AdminGate.IsLoopbackHost(context.Request.Host.Value))
        {
            // DNS rebinding: an outside page whose name now resolves to 127.0.0.1.
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Unexpected Host header");
            return;
        }
        if (clientPaths.Contains(path) || path.Equals("/api/pair", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 404;
            return;
        }
        // MCP and its OAuth flow authenticate on their own (bearer token, PKCE, dashboard approval).
        if (IsMcpPath(path))
        {
            await next();
            return;
        }

        var signedIn = !store.HasPassword || adminGate.IsValid(context.Request.Cookies[AdminGate.CookieName], now);
        var signInPath = path.Equals("/login", StringComparison.OrdinalIgnoreCase)
                         || path.Equals("/api/admin/login", StringComparison.OrdinalIgnoreCase)
                         || path.Equals("/api/admin/session", StringComparison.OrdinalIgnoreCase);
        if (!signedIn && !signInPath)
        {
            if (HttpMethods.IsGet(context.Request.Method) && !path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
                context.Response.Redirect("/login");
            else
                context.Response.StatusCode = 401;
            return;
        }
        if (AdminGate.IsStateChanging(context.Request.Method) && context.Request.Headers[AdminGate.CsrfHeader] != "1")
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync($"Missing {AdminGate.CsrfHeader} header");
            return;
        }
        context.Items[CredentialItemKey] = dashboardCredential;
        await next();
        return;
    }

    // --- The relay listener, reachable from the network ---

    if (IsMcpPath(path))
    {
        await next();
        return;
    }

    if (dashboardOnly.Contains(path) || path.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = 404;
        return;
    }

    if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        && !path.Equals("/api/pair", StringComparison.OrdinalIgnoreCase))
    {
        var reqToken = context.Request.Headers["X-Token"].FirstOrDefault()
                       ?? ExtractBearer(context.Request.Headers["Authorization"].FirstOrDefault())
                       ?? context.Request.Query["token"].FirstOrDefault();
        // A valid token is always served — the throttle only slows down the guessing, so an
        // attacker sharing a NAT address with a real client can never lock that client out.
        var credential = store.Authenticate(reqToken);
        if (credential is null)
        {
            await RejectAsync(context, authThrottle, stats, events, $"401 on {path}", 401, "Invalid token");
            return;
        }

        // A client's token is only good for being a client. Before roles, any machine that ran the
        // client could also send commands to every other machine on the relay.
        var isClientPath = clientPaths.Contains(path);
        if ((credential.Role == CredentialRole.Client && !isClientPath)
            || (credential.Role == CredentialRole.Controller && isClientPath))
        {
            events.Add("auth", credential.Display, $"403 on {path} ({credential.Role.ToString().ToLowerInvariant()} token)");
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync($"A {credential.Role.ToString().ToLowerInvariant()} token may not use {path}");
            return;
        }
        context.Items[CredentialItemKey] = credential;
    }
    await next();
});

// === Client-facing: polling (encrypted) ===

app.MapGet("/api/poll", (HttpRequest req) =>
{
    var session = TouchSession(req, clients, events);
    // Hand out one queued command per poll. Skip any that timed out before being delivered.
    while (session.CommandQueue.TryDequeue(out var pending))
    {
        if (pending.Cancelled) continue;
        // timeoutSeconds tells the client how long this command may run. Clients from before this
        // field existed ignore it and fall back to their own built-in limit.
        return Results.Ok(new
        {
            command = KeyFor(req).EncryptString(pending.Command),
            requestId = pending.RequestId,
            timeoutSeconds = pending.TimeoutSeconds,
        });
    }
    return Results.Ok(new { command = (string?)null });
});

app.MapPost("/api/result", async (HttpRequest req) =>
{
    var session = TouchSession(req, clients, events);
    var requestId = req.Query["requestId"].FirstOrDefault();
    var body = await ReadBodyAsync(req);
    CommandResult result;
    try
    {
        var decryptedBytes = KeyFor(req).Decrypt(body);
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

// One transfer at a time per client, handed out with its id. Uploads go first: they are already
// buffered on the relay, so finishing them frees that memory soonest.
app.MapGet("/api/file-poll", (HttpRequest req) =>
{
    var session = TouchSession(req, clients, events);

    var upload = session.Uploads.Lease();
    if (upload != null)
    {
        var meta = JsonSerializer.Serialize(new
        {
            action = "upload", path = upload.Path, size = upload.Data!.Length, transferId = upload.Id,
        });
        return Results.Ok(new { e = KeyFor(req).EncryptString(meta) });
    }

    var download = session.Downloads.Lease();
    if (download != null)
    {
        var meta = JsonSerializer.Serialize(new
        {
            action = "download", path = download.Path, size = 0, transferId = download.Id,
        });
        return Results.Ok(new { e = KeyFor(req).EncryptString(meta) });
    }

    return Results.Ok(new { e = (string?)null });
});

app.MapGet("/api/file-data", (HttpRequest req) =>
{
    var session = TouchSession(req, clients, events);
    var wanted = req.Query["transferId"].FirstOrDefault();
    var job = session.Uploads.Current;
    // A stale id means the client is asking for a transfer that has already been settled; sending
    // it the current one instead is how the wrong bytes reached the wrong path.
    if (job?.Data == null || (!string.IsNullOrEmpty(wanted) && job.Id != wanted)) return Results.NotFound();
    return SendBytes(KeyFor(req).Encrypt(job.Data));
});

// The client reports here once it has finished writing the file. It calls this whether the write
// succeeded or failed, so an ?error= says which — without it a full disk or a read-only path was
// reported to the caller as a successful upload. Clients from before this field simply omit it.
app.MapPost("/api/file-done", (HttpRequest req) =>
{
    var session = TouchSession(req, clients, events);
    var error = req.Query["error"].FirstOrDefault();
    var job = session.Uploads.Release(req.Query["transferId"].FirstOrDefault());
    job?.Done.TrySetResult(string.IsNullOrEmpty(error) ? null : error);
    return Results.Ok();
});

app.MapPost("/api/file-upload", async (HttpRequest req) =>
{
    var session = TouchSession(req, clients, events);
    var error = req.Query["error"].FirstOrDefault();
    var job = session.Downloads.Release(req.Query["transferId"].FirstOrDefault());
    if (job == null) return Results.Ok();

    if (!string.IsNullOrEmpty(error))
    {
        job.Result.TrySetResult(new FileTransfer { Error = error });
        return Results.Ok();
    }

    var body = await ReadBodyAsync(req);
    try
    {
        var decrypted = KeyFor(req).Decrypt(body);
        job.Result.TrySetResult(new FileTransfer { Data = decrypted });
    }
    catch (Exception ex)
    {
        job.Result.TrySetResult(new FileTransfer { Error = $"[DECRYPT ERROR] {ex.Message}" });
    }
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

    return Results.Ok(await RunCommand(target.Session!, body.Command, body.TimeoutSeconds));
});

// === Relay operations, shared by the REST API and by MCP ===

// Each exec gets its own correlated slot. Multiple execs (same or different sessions) run
// concurrently — the client picks them up one per poll and returns each result by requestId.
async Task<CommandResult> RunCommand(ClientSession session, string command, int requestedTimeout)
{
    var timeout = ExecLimits.Clamp(requestedTimeout);
    var pending = new PendingCommand { Command = command, TimeoutSeconds = timeout };
    session.InFlight[pending.RequestId] = pending;
    session.CommandQueue.Enqueue(pending);
    Interlocked.Increment(ref stats.Execs);
    events.Add("exec", session.Name, Excerpt(command), pending.RequestId);

    var execStartedUtc = DateTime.UtcNow;
    // The name this command was dispatched to. A session can be renamed while the command is out
    // (a client re-registering under a different --name), and the record should say where it went.
    var targetName = session.Name;
    // Outlast the client's own deadline by the grace window: a command killed exactly on time still
    // has its output in flight, and reporting a relay timeout over it would throw that output away.
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout + ExecLimits.RelayGraceSeconds));
    try
    {
        var result = await pending.Tcs.Task.WaitAsync(cts.Token);
        commands.Add(pending.RequestId, execStartedUtc, targetName, command,
            result.Output, result.ExitCode, Elapsed(execStartedUtc));
        return result;
    }
    catch (OperationCanceledException)
    {
        pending.Cancelled = true;
        session.InFlight.TryRemove(pending.RequestId, out _);
        Interlocked.Increment(ref stats.Timeouts);
        events.Add("timeout", targetName, $"no result after {timeout}s", pending.RequestId);
        var timedOut = new CommandResult { Output = $"[TIMEOUT] No response from '{targetName}' after {timeout}s", ExitCode = -1 };
        commands.Add(pending.RequestId, execStartedUtc, targetName, command,
            timedOut.Output, timedOut.ExitCode, Elapsed(execStartedUtc));
        return timedOut;
    }
}

// Hands a file to a client and waits for it to be written. Null on success, else why it failed.
async Task<string?> PushFile(ClientSession session, string remotePath, byte[] data)
{
    Console.WriteLine($"[UPLOAD] {session.Name}: {FormatBytes(data.Length)} -> {remotePath}");
    Interlocked.Increment(ref stats.Uploads);
    Interlocked.Add(ref stats.BytesUploaded, data.Length);
    events.Add("upload", session.Name, $"{FormatBytes(data.Length)} -> {Excerpt(remotePath)}");

    var job = new FileJob { Path = remotePath, Data = data };
    session.Uploads.Enqueue(job);

    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    try
    {
        var error = await job.Done.Task.WaitAsync(cts.Token);
        if (error == null) return null;
        events.Add("upload", session.Name, $"failed -> {Excerpt(remotePath)}");
        return $"Client could not write the file: {error}";
    }
    catch (OperationCanceledException)
    {
        // Only this transfer is abandoned. Clearing the whole slot used to cancel a completely
        // different upload that happened to be in progress.
        session.Uploads.Release(job.Id);
        events.Add("upload", session.Name, $"timeout -> {Excerpt(remotePath)}");
        return "Upload timeout";
    }
}

// Fetches a file from a client. Data is null when it failed, with the reason in Error.
async Task<FileTransfer> PullFile(ClientSession session, string remotePath)
{
    Console.WriteLine($"[DOWNLOAD] {session.Name}: <- {remotePath}");
    Interlocked.Increment(ref stats.Downloads);

    var job = new FileJob { Path = remotePath };
    session.Downloads.Enqueue(job);

    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    try
    {
        // Logged on completion rather than on request: the size is only known once the client has
        // actually sent the bytes, and a history line reading "0 kB" would be a lie either way.
        var result = await job.Result.Task.WaitAsync(cts.Token);
        if (result.Data == null)
        {
            events.Add("download", session.Name, $"failed <- {Excerpt(remotePath)}");
            return new FileTransfer { Error = result.Error ?? "File not found" };
        }
        Interlocked.Add(ref stats.BytesDownloaded, result.Data.Length);
        events.Add("download", session.Name, $"{FormatBytes(result.Data.Length)} <- {Excerpt(remotePath)}");
        return result;
    }
    catch (OperationCanceledException)
    {
        // Abandon only this transfer, not whatever else the client is in the middle of.
        session.Downloads.Release(job.Id);
        events.Add("download", session.Name, $"timeout <- {Excerpt(remotePath)}");
        return new FileTransfer { Error = "Download timeout" };
    }
}

// The client list as /api/clients and the MCP tool both report it.
object ClientList()
{
    var list = clients.Values
        .OrderBy(c => c.Name)
        .Select(c => new
        {
            id = c.Id,
            name = c.Name,
            ip = c.RemoteIp,
            token = c.TokenLabel,
            lastPoll = c.LastPoll,
            secondsAgo = c.LastPoll == DateTime.MinValue ? -1 : (int)(DateTime.UtcNow - c.LastPoll).TotalSeconds,
            connected = c.IsConnected(),
            running = c.InFlight.Count,
            queued = c.CommandQueue.Count,
            served = Interlocked.Read(ref c.CommandsServed),
            state = !c.Uploads.Idle ? "upload" : !c.Downloads.Idle ? "download" : c.IsConnected() ? "idle" : "stale",
            connectedForSeconds = (int)(DateTime.UtcNow - c.ConnectedSince).TotalSeconds
        }).ToList();
    return new { count = list.Count, connected = list.Count(x => x.connected), clients = list };
}

app.MapPost("/api/upload", async (HttpRequest req) =>
{
    var remotePath = req.Query["path"].FirstOrDefault();
    if (string.IsNullOrEmpty(remotePath))
        return Results.BadRequest(new { error = "Missing ?path= parameter" });

    var target = ResolveTarget(req, clients);
    if (target.Error != null)
        return Results.BadRequest(new { error = target.Error });

    var session = target.Session!;

    var data = await ReadBodyAsync(req);
    TrimWhenDone(req, data.Length);

    var error = await PushFile(session, remotePath, data);
    return error switch
    {
        null => Results.Ok(new { status = "ok", size = data.Length, client = session.Name }),
        // A timeout is a failure; answering 200 let every caller treat it as a completed upload.
        "Upload timeout" => Results.Json(new { error }, statusCode: 504),
        _ => Results.Json(new { error }, statusCode: 502),
    };
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

    var result = await PullFile(session, remotePath);
    if (result.Data == null)
        return result.Error == "Download timeout" ? Results.StatusCode(504) : Results.NotFound(new { error = result.Error });
    TrimWhenDone(req, result.Data.Length);
    return SendBytes(result.Data, Path.GetFileName(remotePath));
});

// === Info endpoints ===

app.MapGet("/api/clients", () => Results.Ok(ClientList()));

app.MapGet("/api/events", (HttpRequest req) =>
{
    var limit = int.TryParse(req.Query["limit"].FirstOrDefault(), out var l) && l is > 0 and <= 500 ? l : 100;
    // With --open-status this endpoint answers anonymous callers and shows everything, command
    // lines included. That switch exists precisely to make the dashboard usable without a token,
    // and it is what forces the relay onto loopback (see RelayBinding) — whoever reaches the port
    // is then genuinely on this machine and can read the same commands from the process list.
    var recent = events.Snapshot().TakeLast(limit)
        .Select(e => new
        {
            at = e.AtUtc,
            kind = e.Kind,
            client = e.Client,
            message = e.Message,
            id = e.Id,
        })
        .ToList();
    return Results.Ok(new
    {
        uptimeSeconds = (int)(DateTime.UtcNow - startedUtc).TotalSeconds,
        tls = !noTls,
        tokens = store.Count,
        stats = stats.Snapshot(),
        events = recent,
    });
});

// Output of one command, for the status page's detail view.
app.MapGet("/api/command", (HttpRequest req) =>
{
    var id = req.Query["id"].FirstOrDefault();
    var record = string.IsNullOrEmpty(id) ? null : commands.Get(id);
    return record is null
        ? Results.NotFound(new { error = "No stored output for that command" })
        : Results.Ok(record);
});

// Browser status page, on the dashboard listener only (see the middleware). Everything it shows
// comes from the JSON endpoints above.
app.MapGet("/ui", () => Results.Content(StatusPage.Html, "text/html; charset=utf-8"));

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
            ip = s.RemoteIp,
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

var relay = new RelayContext(store, storePath, pairings, adminGate, authThrottle, stats, clients, events,
    port, adminPort, noTls, certSha256);
AdminEndpoints.Map(app, relay);
McpEndpoints.Map(app, relay, new OAuthServer(store), new McpOps(
    name => ResolveClient(name, clients), RunCommand, PushFile, PullFile, ClientList));
app.MapGet("/manage", () => Results.Content(AdminPage.Manage, "text/html; charset=utf-8"));
app.MapGet("/login", () => Results.Content(AdminPage.Login, "text/html; charset=utf-8"));

// The dashboard on its own listener; on the relay port, a plain note of what this is and nothing
// about who is connected.
app.MapGet("/", (HttpContext context) => IsDashboardRequest(context, adminPort)
    ? Results.Content(StatusPage.Html, "text/html; charset=utf-8")
    : Results.Text(
        $"Remote CMD Relay Server {RemoteCmd.Shared.VersionInfo.Version}\n" +
        $"Encryption: AES-256-GCM | TLS: {(noTls ? "off" : "self-signed")}\n" +
        "\n" +
        "GET  /api/status[?client=X]                - Check client(s)\n" +
        "GET  /api/clients                          - List all clients\n" +
        "GET  /api/events[?limit=N]                 - Recent relay events + counters\n" +
        "GET  /api/command?id=X                     - Output of one command\n" +
        "POST /api/exec[?client=X]                  - Run command {\"command\":\"...\"}\n" +
        "POST /api/upload?path=...[&client=X]       - Upload file (binary body)\n" +
        "GET  /api/download?path=...[&client=X]     - Download file\n" +
        "POST /mcp                                  - MCP (Streamable HTTP, OAuth)\n" +
        "All /api endpoints need a controller token (X-Token header or Bearer).\n" +
        "The dashboard listens on this machine only.", "text/plain"));

app.Run();
return 0;

// === Helpers ===

/// <summary>The credential this request authenticated with.</summary>
static Credential CredentialOf(HttpRequest req)
    => (Credential)req.HttpContext.Items["remotecmd.credential"]!;

/// <summary>AES key of the credential this request authenticated with.</summary>
static CryptoKey KeyFor(HttpRequest req) => CredentialOf(req).Crypto;

/// <summary>
/// True for requests that arrived on the loopback dashboard listener. The test host reports no
/// local port, so tests mark dashboard requests by setting it themselves.
/// </summary>
static bool IsDashboardRequest(HttpContext context, int adminPort)
    => context.Connection.LocalPort == adminPort;

/// <summary>Refuses a request, counting it towards the sender's lockout.</summary>
static async Task RejectAsync(HttpContext context, AuthThrottle throttle, RelayStats stats, EventLog events,
    string what, int status, string message)
{
    var peer = context.Connection.RemoteIpAddress?.ToString() ?? "?";
    var now = DateTime.UtcNow;
    Interlocked.Increment(ref stats.AuthFailures);
    var justLocked = throttle.RecordFailure(peer, now);
    var throttled = justLocked || throttle.IsLockedOut(peer, now);
    events.Add("auth", peer, throttled ? $"blocked: {what}" : what);
    context.Response.StatusCode = throttled ? 429 : status;
    if (throttled) context.Response.Headers.RetryAfter = (AuthThrottle.LockoutMinutes * 60).ToString();
    await context.Response.WriteAsync(throttled ? "Too many failed attempts" : message);
}

static bool IsMcpPath(string path)
    => path.Equals("/mcp", StringComparison.OrdinalIgnoreCase)
       || path.StartsWith("/oauth/", StringComparison.OrdinalIgnoreCase)
       || path.StartsWith("/.well-known/", StringComparison.OrdinalIgnoreCase);

static int? EnvPort(string name)
    => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v is > 0 and < 65536 ? v : null;

/// <summary>
/// Byte count in the largest unit that still reads honestly, so a 691-byte file shows as "691 B"
/// instead of being rounded down to "0 kB". Invariant culture keeps the decimal point stable.
/// </summary>
static string FormatBytes(long bytes)
{
    const long kB = 1024, MB = kB * 1024, GB = MB * 1024;
    return bytes < kB ? $"{bytes} B"
        : bytes < MB ? FormattableString.Invariant($"{bytes / (double)kB:0.#} kB")
        : bytes < GB ? FormattableString.Invariant($"{bytes / (double)MB:0.#} MB")
        : FormattableString.Invariant($"{bytes / (double)GB:0.##} GB");
}

/// <summary>Milliseconds since a start stamp, floored at zero for a backwards clock.</summary>
static int Elapsed(DateTime startedUtc)
    => (int)Math.Max(0, (DateTime.UtcNow - startedUtc).TotalMilliseconds);

/// <summary>
/// One-line, length-capped form of user input for the event history.
///
/// The cap used to be 60 characters, which cut nearly every real command and every absolute path in
/// half — a history line reading "cd /Users/x/project &amp;&amp; tar -xzf /Users/x/pro…" tells the reader
/// nothing they could not have guessed. The history is a ring of 500 entries, so even at this
/// length it costs a few hundred kilobytes at worst.
/// </summary>
const int ExcerptLength = 400;

static string Excerpt(string s)
{
    s = s.ReplaceLineEndings(" ").Trim();
    return s.Length <= ExcerptLength ? s : s[..(ExcerptLength - 1)] + "…";
}

static string? ExtractBearer(string? authHeader)
{
    if (string.IsNullOrEmpty(authHeader)) return null;
    const string prefix = "Bearer ";
    return authHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        ? authHeader[prefix.Length..].Trim()
        : null;
}

static ClientSession TouchSession(HttpRequest req, ConcurrentDictionary<string, ClientSession> clients, EventLog events)
{
    var credential = CredentialOf(req);
    var clientId = req.Query["clientId"].FirstOrDefault();
    var name = req.Query["name"].FirstOrDefault();

    // A paired machine is who its credential says it is. With a shared token, anyone holding it could
    // poll under another machine's id and be handed the commands meant for that machine.
    if (credential.Role == CredentialRole.Client)
    {
        clientId = "c-" + credential.Id;
        name = credential.Label;
    }
    else if (string.IsNullOrEmpty(clientId))
    {
        // Legacy client without clientId: synthesize stable id from remote IP
        var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        clientId = "legacy-" + ip;
        name ??= ip;
    }
    name ??= clientId;

    var isNew = !clients.ContainsKey(clientId);
    var session = clients.GetOrAdd(clientId, id => new ClientSession { Id = id, Name = name });
    var wasOffline = !isNew && !session.IsConnected();

    session.TokenLabel = credential.Display;
    session.CredentialId = credential.Id;
    session.RemoteIp = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
    if (isNew) events.Add("connect", name, $"new session {clientId[..Math.Min(8, clientId.Length)]} (token {session.TokenLabel})");
    else if (wasOffline) events.Add("connect", name, $"back after {(int)(DateTime.UtcNow - session.LastPoll).TotalSeconds}s");

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

// Results.File(byte[]) hands the whole array to Kestrel in one write, which copies all of it into
// the pinned buffer pool before backpressure applies. That pool never shrinks on .NET 9, so every
// large transfer left its full size behind for good. Streaming copies in small chunks that wait
// for the socket, keeping the pool at a few buffers per connection.
static IResult SendBytes(byte[] data, string? fileName = null)
    => Results.Stream(new MemoryStream(data, writable: false), "application/octet-stream", fileName);

// The transfer's arrays stay reachable until the response has gone out, so the trim waits for that.
static void TrimWhenDone(HttpRequest req, long bytes)
    => req.HttpContext.Response.OnCompleted(() =>
    {
        MemoryTrim.AfterTransfer(bytes);
        return Task.CompletedTask;
    });

// A growing MemoryStream doubles its buffer on the way up and ToArray copies it once more, so a
// 150 MB upload used to churn through several large-object-heap arrays for one file. With a known
// length the body lands in a single array of exactly that size. Kestrel only enforces
// MaxRequestBodySize once the body is read, so the declared length is checked against it first —
// otherwise a header alone could make the relay allocate whatever it claims.
static async Task<byte[]> ReadBodyAsync(HttpRequest req)
{
    var max = req.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize;
    if (req.ContentLength is long length && (max == null || length <= max))
    {
        var buffer = new byte[length];
        await req.Body.ReadExactlyAsync(buffer);
        return buffer;
    }
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    return ms.ToArray();
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
    {
        // An id we no longer hold belongs to a command that already timed out. Falling through to
        // the oldest-waiting command would hand that stale output to whoever is waiting now — the
        // caller of a completely different exec gets another command's stdout and exit code.
        session.InFlight.TryRemove(requestId, out pending);
    }
    else
    {
        // Legacy clients omit the id and only ever run one command at a time, so the oldest
        // in-flight command is necessarily the one they are answering.
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
    => ResolveClient(req.Query["client"].FirstOrDefault(), clients);

/// <summary>
/// The session a command goes to: the named one, or the only connected one when no name is given.
/// </summary>
static TargetResolution ResolveClient(string? clientParam, ConcurrentDictionary<string, ClientSession> clients)
{
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

// === Models ===

/// <summary>Zero or less for <see cref="TimeoutSeconds"/> means the relay's default applies.</summary>
public record CommandRequest(string Command, int TimeoutSeconds = 0);

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
    public string TokenLabel { get; set; } = "";  // masked token this client authenticates with
    public string CredentialId { get; set; } = "";
    public string RemoteIp { get; set; } = "";    // address the client last polled from
    public long CommandsServed;   // total results delivered; mutable field for Interlocked

    // Commands queued for the next poll(s), and those handed out awaiting a result (keyed by id).
    public ConcurrentQueue<PendingCommand> CommandQueue { get; } = new();
    public ConcurrentDictionary<string, PendingCommand> InFlight { get; } = new();

    // Queued rather than single-slot: two callers transferring to the same machine at once used to
    // overwrite each other's transfer, and the client wrote one file's bytes to the other's path.
    public TransferQueue Uploads { get; } = new();
    public TransferQueue Downloads { get; } = new();

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

    /// <summary>How long the client may spend on this command, already clamped by the relay.</summary>
    public int TimeoutSeconds { get; init; } = ExecLimits.DefaultSeconds;
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
