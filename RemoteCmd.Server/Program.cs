using System.Collections.Concurrent;
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

// Key under which the auth middleware stashes the token that matched, for the handlers to pick up.
const string TokenItemKey = "remotecmd.token";

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 200_000_000);

// Every positional argument is a token: the relay accepts them all at once, so clients can be
// migrated to a new token without a window where either the old or the new one is refused.
// Position must not matter — none of the switches take a value, and stopping at the first one meant
// "RemoteCmd.Server.exe --dashboard mytoken" silently threw the token away and invented a random
// one, leaving every client locked out with no error to explain it.
var tokens = args.Where(a => !a.StartsWith('-')).Distinct(StringComparer.Ordinal).ToList();
if (tokens.Count == 0)
{
    var fromEnv = Environment.GetEnvironmentVariable("REMOTECMD_TOKENS")
                  ?? Environment.GetEnvironmentVariable("REMOTECMD_TOKEN");
    if (!string.IsNullOrWhiteSpace(fromEnv))
        tokens = fromEnv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Distinct(StringComparer.Ordinal).ToList();
}
if (tokens.Count == 0) tokens.Add(Guid.NewGuid().ToString("N")[..16]);
var token = tokens[0];
var noTls = args.Contains("--no-tls")
    || string.Equals(Environment.GetEnvironmentVariable("REMOTECMD_NO_TLS"), "1", StringComparison.Ordinal)
    || string.Equals(Environment.GetEnvironmentVariable("REMOTECMD_NO_TLS"), "true", StringComparison.OrdinalIgnoreCase);
var dashboard = args.Contains("--dashboard")
    || string.Equals(Environment.GetEnvironmentVariable("REMOTECMD_DASHBOARD"), "1", StringComparison.Ordinal);
// Read-only status without a token: the web dashboard then just opens. Everything that touches a
// machine — exec, file transfer, the client protocol — still needs one.
var openStatus = args.Contains("--open-status")
    || string.Equals(Environment.GetEnvironmentVariable("REMOTECMD_OPEN_STATUS"), "1", StringComparison.Ordinal);

const int MinTokenLength = 12;
var allowShortToken = string.Equals(Environment.GetEnvironmentVariable("REMOTECMD_ALLOW_SHORT_TOKEN"), "1", StringComparison.Ordinal);
foreach (var t in tokens.Where(t => t.Length < MinTokenLength && !allowShortToken))
{
    Console.Error.WriteLine($"[FATAL] Token too short ({t.Length} chars). Minimum is {MinTokenLength}. Set REMOTECMD_ALLOW_SHORT_TOKEN=1 to bypass (NOT RECOMMENDED).");
    return 2;
}

// Session GC: remove clients that have not polled for longer than this threshold.
var staleAfter = TimeSpan.FromHours(1);

// Listen port: default 7890, overridable via REMOTECMD_PORT (CI / multi-instance).
var port = int.TryParse(Environment.GetEnvironmentVariable("REMOTECMD_PORT"), out var p) && p is > 0 and < 65536
    ? p
    : 7890;

// Always every interface: clients poll the relay from other machines. The open, tokenless view is
// narrowed in the auth middleware instead — restricting the listener would take the clients down
// with it, because the bind covers the whole server and not just /ui.
if (noTls)
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}
else
{
    var certificate = RelayCertificate.LoadOrCreate(
        Path.Combine(AppContext.BaseDirectory, "remotecmd.pfx"), Console.Out);

    builder.WebHost.UseUrls($"https://0.0.0.0:{port}");
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

// Each token has its own AES key; the client-facing handlers pick the key of the token the
// polling client authenticated with (KeyFor). The default here only serves legacy call sites.
Crypto.Init(token);

var startedUtc = DateTime.UtcNow;
var clients = new ConcurrentDictionary<string, ClientSession>();
var events = new EventLog();
var commands = new CommandLog();
var stats = new RelayStats();
var authThrottle = new AuthThrottle();

var protocol = noTls ? "http" : "https";
Console.WriteLine($"=== Remote CMD Relay Server {RemoteCmd.Shared.VersionInfo.Version} ===");
Console.WriteLine($"Listening on: {protocol}://0.0.0.0:{port}");
Console.WriteLine($"Tokens ({tokens.Count}): {string.Join(", ", tokens)}");
Console.WriteLine($"TLS: {(noTls ? "disabled" : "enabled (self-signed)")}");
Console.WriteLine($"Encryption: AES-256-GCM (always on)");
Console.WriteLine($"Multi-client: enabled");
Console.WriteLine($"Session GC threshold: {staleAfter.TotalMinutes:N0} minutes");
Console.WriteLine($"Live dashboard: {(dashboard ? "on (--dashboard)" : "off (add --dashboard)")}");
Console.WriteLine(openStatus
    ? $"Status page: {protocol}://<this-host>:{port}/ui  (no token needed from this machine; a token from anywhere else)"
    : $"Status page: {protocol}://<this-host>:{port}/ui");
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

// Auth middleware. Any configured token is accepted; the one that matched is stashed on the
// request so the client-facing handlers encrypt with that token's key.
// Routing matches these routes without regard to case, so the gate in front of them must do the
// same: a case-sensitive check let "/API/exec" miss the gate entirely and run commands untokened.
var openPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "/api/status", "/api/clients", "/api/events", "/api/command",
};

app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    // /ui itself is a static shell with no data in it; it asks for the token in the browser and
    // sends it as a header on its own API calls, so it never has to travel in a URL.
    var readOnly = openPaths.Contains(path.TrimEnd('/'));
    if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
    {
        var reqToken = context.Request.Query["token"].FirstOrDefault()
                       ?? context.Request.Headers["X-Token"].FirstOrDefault()
                       ?? ExtractBearer(context.Request.Headers["Authorization"].FirstOrDefault());
        // A valid token is always served — the throttle only slows down the guessing, so an
        // attacker sharing a NAT address with a real client can never lock that client out.
        var matched = tokens.FirstOrDefault(t => TokensEqual(reqToken, t));
        if (matched is not null)
        {
            // Recorded even on the open endpoints, so a caller that did authenticate gets the full
            // history rather than the redacted one an anonymous viewer sees.
            context.Items[TokenItemKey] = matched;
        }
        // An open relay hands the overview to whoever asks from this machine with no token at all —
        // the point of the switch is that the dashboard just opens locally. From anywhere else a
        // token is still required, because the history carries command lines and the stored output
        // carries whatever those commands printed. Offering a WRONG token is a failed attempt
        // everywhere, so guessing can never dodge the throttle by aiming at the open endpoints.
        else if (!OpenStatus.AllowsAnonymous(openStatus, readOnly, reqToken, context.Connection.RemoteIpAddress))
        {
            var peer = context.Connection.RemoteIpAddress?.ToString() ?? "?";
            var now = DateTime.UtcNow;
            Interlocked.Increment(ref stats.AuthFailures);
            var justLocked = authThrottle.RecordFailure(peer, now);
            var throttled = justLocked || authThrottle.IsLockedOut(peer, now);
            events.Add("auth", peer, throttled ? $"blocked on {path}" : $"401 on {path}");

            context.Response.StatusCode = throttled ? 429 : 401;
            if (throttled) context.Response.Headers.RetryAfter = (AuthThrottle.LockoutMinutes * 60).ToString();
            await context.Response.WriteAsync(throttled ? "Too many failed attempts" : "Invalid token");
            return;
        }
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
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    CommandResult result;
    try
    {
        var decryptedBytes = KeyFor(req).Decrypt(ms.ToArray());
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
    return Results.File(KeyFor(req).Encrypt(job.Data), "application/octet-stream");
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

    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    try
    {
        var decrypted = KeyFor(req).Decrypt(ms.ToArray());
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

    var session = target.Session!;

    // Each exec gets its own correlated slot. Multiple execs (same or different sessions) run
    // concurrently — the client picks them up one per poll and returns each result by requestId.
    var timeout = ExecLimits.Clamp(body.TimeoutSeconds);
    var pending = new PendingCommand { Command = body.Command, TimeoutSeconds = timeout };
    session.InFlight[pending.RequestId] = pending;
    session.CommandQueue.Enqueue(pending);
    Interlocked.Increment(ref stats.Execs);
    events.Add("exec", session.Name, Excerpt(body.Command), pending.RequestId);

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
        commands.Add(pending.RequestId, execStartedUtc, targetName, body.Command,
            result.Output, result.ExitCode, Elapsed(execStartedUtc));
        return Results.Ok(result);
    }
    catch (OperationCanceledException)
    {
        pending.Cancelled = true;
        session.InFlight.TryRemove(pending.RequestId, out _);
        Interlocked.Increment(ref stats.Timeouts);
        events.Add("timeout", targetName, $"no result after {timeout}s", pending.RequestId);
        var timedOut = new CommandResult { Output = $"[TIMEOUT] No response from '{targetName}' after {timeout}s", ExitCode = -1 };
        commands.Add(pending.RequestId, execStartedUtc, targetName, body.Command,
            timedOut.Output, timedOut.ExitCode, Elapsed(execStartedUtc));
        return Results.Ok(timedOut);
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
        if (error != null)
        {
            events.Add("upload", session.Name, $"failed -> {Excerpt(remotePath)}");
            return Results.Json(new { error = $"Client could not write the file: {error}" }, statusCode: 502);
        }
        return Results.Ok(new { status = "ok", size = data.Length, client = session.Name });
    }
    catch (OperationCanceledException)
    {
        // Only this transfer is abandoned. Clearing the whole slot used to cancel a completely
        // different upload that happened to be in progress.
        session.Uploads.Release(job.Id);
        events.Add("upload", session.Name, $"timeout -> {Excerpt(remotePath)}");
        // A timeout is a failure; answering 200 let every caller treat it as a completed upload.
        return Results.Json(new { error = "Upload timeout" }, statusCode: 504);
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
            return Results.NotFound(new { error = result.Error ?? "File not found" });
        }
        Interlocked.Add(ref stats.BytesDownloaded, result.Data.Length);
        events.Add("download", session.Name, $"{FormatBytes(result.Data.Length)} <- {Excerpt(remotePath)}");
        return Results.File(result.Data, "application/octet-stream", Path.GetFileName(remotePath));
    }
    catch (OperationCanceledException)
    {
        // Abandon only this transfer, not whatever else the client is in the middle of.
        session.Downloads.Release(job.Id);
        events.Add("download", session.Name, $"timeout <- {Excerpt(remotePath)}");
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
    return Results.Ok(new { count = list.Count, connected = list.Count(x => x.connected), clients = list });
});

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
        tokens = tokens.Count,
        stats = stats.Snapshot(),
        events = recent,
    });
});

// Output of one command, for the status page's detail view. Open together with the rest of the
// dashboard under --open-status: the detail is the whole point of the page, and that mode binds to
// loopback, so it hides nothing that is not already readable on the machine itself.
app.MapGet("/api/command", (HttpRequest req) =>
{
    var id = req.Query["id"].FirstOrDefault();
    var record = string.IsNullOrEmpty(id) ? null : commands.Get(id);
    return record is null
        ? Results.NotFound(new { error = "No stored output for that command" })
        : Results.Ok(record);
});

// Browser status page. Everything it needs comes from the two JSON endpoints above, and it reuses
// the caller's own query string, so the token is never baked into the markup.
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

// Unauthenticated: says what this is and nothing about who is connected.
app.MapGet("/", () => Results.Text(
    $"Remote CMD Relay Server {RemoteCmd.Shared.VersionInfo.Version} (multi-client)\n" +
    $"Encryption: AES-256-GCM | TLS: {(noTls ? "off" : "self-signed")}\n\n" +
    "GET  /ui                                   - Status page (sessions, history, stats)\n" +
    "GET  /api/status[?client=X]                - Check client(s)\n" +
    "GET  /api/clients                          - List all clients\n" +
    "GET  /api/events[?limit=N]                 - Recent relay events + counters\n" +
    "GET  /api/command?id=X                     - Output of one command\n" +
    "POST /api/exec[?client=X]                  - Run command {\"command\":\"...\"}\n" +
    "POST /api/upload?path=...[&client=X]       - Upload file (binary body)\n" +
    "GET  /api/download?path=...[&client=X]     - Download file\n" +
    (openStatus
        ? "Token (query/header/Bearer) needed for everything except the read-only status endpoints,\n" +
          "which --open-status serves to anyone who reaches this port."
        : "All endpoints need token (query/header/Bearer)."), "text/plain"));

app.Run();
return 0;

// === Helpers ===

/// <summary>AES key of the token this request authenticated with.</summary>
static CryptoKey KeyFor(HttpRequest req)
    => Crypto.For((string)req.HttpContext.Items[TokenItemKey]!);

/// <summary>Short, non-secret label of a token for dashboards and logs.</summary>
static string MaskToken(string t)
    => t.Length <= 4 ? new string('*', t.Length) : $"{t[..2]}…{t[^2..]}";

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

static bool TokensEqual(string? a, string b)
{
    if (a == null || a.Length != b.Length) return false;
    var aBytes = System.Text.Encoding.UTF8.GetBytes(a);
    var bBytes = System.Text.Encoding.UTF8.GetBytes(b);
    return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
}

static ClientSession TouchSession(HttpRequest req, ConcurrentDictionary<string, ClientSession> clients, EventLog events)
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

    var isNew = !clients.ContainsKey(clientId);
    var session = clients.GetOrAdd(clientId, id => new ClientSession { Id = id, Name = name });
    var wasOffline = !isNew && !session.IsConnected();

    session.TokenLabel = MaskToken((string)req.HttpContext.Items[TokenItemKey]!);
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
