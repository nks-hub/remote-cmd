using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:7890");
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 200_000_000); // 200MB

var app = builder.Build();

var token = args.Length > 0 ? args[0] : Guid.NewGuid().ToString("N")[..12];

string? pendingCommand = null;
TaskCompletionSource<CommandResult>? resultTcs = null;
DateTime lastClientPoll = DateTime.MinValue;
var commandLock = new SemaphoreSlim(1, 1);

// File transfer state
FileTransfer? pendingUpload = null;
TaskCompletionSource<bool>? uploadTcs = null;
FileTransfer? pendingDownload = null;
TaskCompletionSource<FileTransfer>? downloadTcs = null;

Console.WriteLine("=== Remote CMD Relay Server ===");
Console.WriteLine($"Listening on: http://0.0.0.0:7890");
Console.WriteLine($"Token: {token}");
Console.WriteLine();
Console.WriteLine("Client setup (run on target machine):");
Console.WriteLine($"  RemoteCmd.Client.exe <THIS_SERVER_IP> {token}");
Console.WriteLine();
Console.WriteLine("Commands:");
Console.WriteLine($"  curl -X POST http://localhost:7890/api/exec?token={token} -H \"Content-Type: application/json\" -d \"{{\\\"command\\\":\\\"hostname\\\"}}\"");
Console.WriteLine();
Console.WriteLine("File transfer:");
Console.WriteLine($"  curl -X POST \"http://localhost:7890/api/upload?token={token}&path=C:\\dest\\file.zip\" --data-binary @local.zip");
Console.WriteLine($"  curl -o local.zip \"http://localhost:7890/api/download?token={token}&path=C:\\remote\\file.zip\"");
Console.WriteLine();

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

// === Command execution ===

app.MapGet("/api/poll", () =>
{
    lastClientPoll = DateTime.UtcNow;
    if (pendingCommand != null)
    {
        var cmd = pendingCommand;
        pendingCommand = null;
        return Results.Ok(new { command = cmd });
    }
    return Results.Ok(new { command = (string?)null });
});

app.MapPost("/api/result", async (HttpRequest req) =>
{
    var result = await req.ReadFromJsonAsync<CommandResult>();
    if (result != null && resultTcs != null)
        resultTcs.TrySetResult(result);
    return Results.Ok();
});

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

// === File transfer: Upload (local → remote) ===

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

// Client polls for pending upload
app.MapGet("/api/file-poll", () =>
{
    lastClientPoll = DateTime.UtcNow;

    if (pendingUpload != null)
        return Results.Ok(new { action = "upload", path = pendingUpload.Path, size = pendingUpload.Data!.Length });

    if (downloadTcs != null)
    {
        var path = pendingDownload?.Path;
        return Results.Ok(new { action = "download", path, size = 0 });
    }

    return Results.Ok(new { action = (string?)null, path = (string?)null, size = 0 });
});

// Client downloads file data for upload-to-remote
app.MapGet("/api/file-data", () =>
{
    if (pendingUpload?.Data == null)
        return Results.NotFound();

    var data = pendingUpload.Data;
    return Results.File(data, "application/octet-stream");
});

// Client confirms upload complete
app.MapPost("/api/file-done", () =>
{
    pendingUpload = null;
    uploadTcs?.TrySetResult(true);
    return Results.Ok();
});

// === File transfer: Download (remote → local) ===

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

// Client uploads file data for download-from-remote
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
    downloadTcs?.TrySetResult(new FileTransfer { Data = ms.ToArray() });
    pendingDownload = null;
    downloadTcs = null;
    return Results.Ok();
});

// === Status ===

app.MapGet("/api/status", () =>
{
    var connected = (DateTime.UtcNow - lastClientPoll).TotalSeconds < 10;
    return Results.Ok(new
    {
        clientConnected = connected,
        lastPoll = lastClientPoll,
        secondsAgo = connected ? (int)(DateTime.UtcNow - lastClientPoll).TotalSeconds : -1
    });
});

app.MapGet("/", () => Results.Text(
    "Remote CMD Relay Server\n" +
    "GET  /api/status                    - Check client\n" +
    "POST /api/exec                      - Run command {\"command\":\"...\", \"timeoutSeconds\":30}\n" +
    "POST /api/upload?path=C:\\dest\\f.zip  - Upload file to remote (--data-binary @local.zip)\n" +
    "GET  /api/download?path=C:\\src\\f.zip - Download file from remote\n" +
    "All endpoints need ?token=<TOKEN>", "text/plain"));

app.Run();

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
