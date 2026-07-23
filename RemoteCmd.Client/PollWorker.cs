using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteCmd.Shared;

namespace RemoteCmd.Client;

/// <summary>
/// Long-running poller: fetches commands and file-transfer jobs from the relay,
/// executes them locally and posts results back. Runs identically as a console
/// app, a Windows Service or a systemd unit.
/// </summary>
public sealed class PollWorker : BackgroundService
{
    private const int CommandTimeoutMs = 60_000;
    // After the process exits we wait at most this long for the pipes to drain: a detached child
    // (e.g. `nohup ... &`) can keep the write end open forever, so we return what we have and move on.
    private const int OutputGraceMs = 1_500;

    private readonly ClientConfig _config;
    private readonly ILogger<PollWorker> _log;
    private readonly ClientStats? _stats;

    // Single-flight guard so a large file transfer runs off the poll loop without being re-dispatched.
    private volatile bool _fileBusy;

    public PollWorker(ClientConfig config, ILogger<PollWorker> log, ClientStats? stats = null)
    {
        _config = config;
        _log = log;
        _stats = stats;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var baseUrl = _config.BaseUrl;
        var clientId = ResolveClientId(_config.Name);
        var qs = $"?token={_config.Token}&clientId={clientId}&name={Uri.EscapeDataString(_config.Name)}";

        var pollUrl = $"{baseUrl}/api/poll{qs}";
        var resultUrl = $"{baseUrl}/api/result{qs}";
        var filePollUrl = $"{baseUrl}/api/file-poll{qs}";
        var fileDataUrl = $"{baseUrl}/api/file-data{qs}";
        var fileDoneUrl = $"{baseUrl}/api/file-done{qs}";
        var fileUploadUrl = $"{baseUrl}/api/file-upload{qs}";

        Crypto.Init(_config.Token);

        var handler = new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
            PooledConnectionLifetime = TimeSpan.FromSeconds(30),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 32,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };

        _log.LogInformation("Remote CMD Client started. Server={Server} Name={Name} Id={Id} Shell={Shell}",
            baseUrl, _config.Name, clientId, DefaultShell());

        var retryDelay = 1;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var gotWork = false;

                // 1. Poll for a command. Never block the loop on execution: run it on its own task
                //    and post the result (with its requestId) when it finishes, so the heartbeat
                //    keeps ticking and further commands are picked up concurrently.
                var response = await http.GetFromJsonAsync<PollResponse>(pollUrl, stoppingToken);
                _stats?.MarkPoll();
                if (response?.Command != null)
                {
                    gotWork = true;
                    var command = Crypto.DecryptString(response.Command);
                    var requestId = response.RequestId;
                    _log.LogInformation("[CMD{Id}] {Command}",
                        requestId is null ? "" : " " + requestId[..Math.Min(8, requestId.Length)], command);
                    _ = HandleCommandAsync(http, resultUrl, command, requestId, stoppingToken);
                }

                // 2. Poll for a file transfer, also dispatched off-loop (single-flight) so a large
                //    transfer never stalls command polling or the heartbeat.
                if (!_fileBusy)
                {
                    var filePoll = await http.GetFromJsonAsync<EncryptedFilePoll>(filePollUrl, stoppingToken);
                    if (filePoll?.E != null)
                    {
                        var meta = JsonSerializer.Deserialize<FilePollMeta>(Crypto.DecryptString(filePoll.E));
                        if (meta?.Action == "upload" && meta.Path != null)
                        {
                            _fileBusy = true;
                            gotWork = true;
                            _ = RunFileTransferAsync(ReceiveFile(http, meta, fileDataUrl, fileDoneUrl, stoppingToken));
                        }
                        else if (meta?.Action == "download" && meta.Path != null)
                        {
                            _fileBusy = true;
                            gotWork = true;
                            _ = RunFileTransferAsync(SendFile(http, meta, fileUploadUrl, stoppingToken));
                        }
                    }
                }

                retryDelay = 1;
                // Drain a burst of queued commands quickly; otherwise settle to the idle cadence.
                await Task.Delay(gotWork ? 50 : 800, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning("[ERROR] {Message} - retry in {Delay}s", ex.Message, retryDelay);
                try { await Task.Delay(retryDelay * 1000, stoppingToken); }
                catch (OperationCanceledException) { break; }
                retryDelay = Math.Min(retryDelay * 2, 30);
            }
        }

        _log.LogInformation("Remote CMD Client stopped.");
    }

    private async Task RunFileTransferAsync(Task transfer)
    {
        try { await transfer; }
        finally { _fileBusy = false; }
    }

    private async Task HandleCommandAsync(HttpClient http, string resultUrl, string command, string? requestId, CancellationToken ct)
    {
        _stats?.ExecStarted();
        try
        {
            var (output, exitCode) = await ExecuteCommand(command, ct);
            if (exitCode != 0) _log.LogWarning("[EXIT {Code}]", exitCode);

            var resultJson = JsonSerializer.Serialize(new CommandResult { Output = output, ExitCode = exitCode });
            var encrypted = Crypto.Encrypt(Encoding.UTF8.GetBytes(resultJson));
            var url = requestId is null ? resultUrl : $"{resultUrl}&requestId={requestId}";
            using var content = new ByteArrayContent(encrypted);
            await http.PostAsync(url, content, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning("[CMD ERROR] {Message}", ex.Message);
        }
        finally
        {
            _stats?.ExecFinished();
        }
    }

    private async Task ReceiveFile(HttpClient http, FilePollMeta meta, string fileDataUrl, string fileDoneUrl, CancellationToken ct)
    {
        _log.LogInformation("[FILE] Receiving {Mb}MB -> {Path}", meta.Size / 1024 / 1024, meta.Path);
        try
        {
            var fileData = Crypto.Decrypt(await http.GetByteArrayAsync(fileDataUrl, ct));
            var dir = Path.GetDirectoryName(meta.Path!);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(meta.Path!, fileData, ct);
            await http.PostAsync(fileDoneUrl, null, ct);
            _log.LogInformation("[FILE] Saved {Mb}MB -> {Path}", fileData.Length / 1024 / 1024, meta.Path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError("[FILE ERROR] {Message}", ex.Message);
            await http.PostAsync(fileDoneUrl, null, ct);
        }
    }

    private async Task SendFile(HttpClient http, FilePollMeta meta, string fileUploadUrl, CancellationToken ct)
    {
        _log.LogInformation("[FILE] Uploading <- {Path}", meta.Path);
        try
        {
            if (!File.Exists(meta.Path))
            {
                await http.PostAsync($"{fileUploadUrl}&error={Uri.EscapeDataString("File not found: " + meta.Path)}", null, ct);
                return;
            }
            var fileData = await File.ReadAllBytesAsync(meta.Path!, ct);
            using var content = new ByteArrayContent(Crypto.Encrypt(fileData));
            await http.PostAsync(fileUploadUrl, content, ct);
            _log.LogInformation("[FILE] Uploaded {Mb}MB <- {Path}", fileData.Length / 1024 / 1024, meta.Path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError("[FILE ERROR] {Message}", ex.Message);
            await http.PostAsync($"{fileUploadUrl}&error={Uri.EscapeDataString(ex.Message)}", null, ct);
        }
    }

    internal static string DefaultShell()
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "powershell.exe" : "/bin/bash";

    private static async Task<(string output, int exitCode)> ExecuteCommand(string command, CancellationToken ct)
    {
        try
        {
            var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            using var process = new Process();
            process.StartInfo = isWindows
                ? new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -Command \"{command.Replace("\"", "\\\"")}\"",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
                : new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    ArgumentList = { "-c", command },
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

            process.Start();

            // Give the child a closed stdin (immediate EOF) so it can never block waiting for input.
            try { process.StandardInput.Close(); } catch { /* ignore */ }

            // Pump the pipes incrementally. ReadToEnd would hang forever when a detached grandchild
            // inherits the write end and keeps it open (the classic `nohup long_running &` case).
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var outPump = PumpAsync(process.StandardOutput, stdout, ct);
            var errPump = PumpAsync(process.StandardError, stderr, ct);

            var exited = await WaitForExitAsync(process, CommandTimeoutMs, ct);
            var killed = false;
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                killed = true;
            }

            // Wait for the pipes to drain, but only briefly — a detached child may hold them open.
            await Task.WhenAny(Task.WhenAll(outPump, errPump), Task.Delay(OutputGraceMs, ct));

            string outStr, errStr;
            lock (stdout) outStr = stdout.ToString();
            lock (stderr) errStr = stderr.ToString();

            var combined = outStr;
            if (!string.IsNullOrWhiteSpace(errStr))
                combined += "\n[STDERR]\n" + errStr;
            combined = combined.TrimEnd();

            if (killed)
            {
                const string note = "[KILLED] Command exceeded 60s timeout";
                return (combined.Length == 0 ? note : combined + "\n" + note, -1);
            }

            return (combined, process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            return ("[CANCELLED]", -1);
        }
        catch (Exception ex)
        {
            return ($"[EXEC ERROR] {ex.Message}", -1);
        }
    }

    /// <summary>Copy a redirected stream into <paramref name="sink"/> until EOF, tolerating pipe teardown.</summary>
    private static async Task PumpAsync(StreamReader reader, StringBuilder sink, CancellationToken ct)
    {
        var buffer = new char[4096];
        try
        {
            int n;
            while ((n = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
                lock (sink) sink.Append(buffer, 0, n);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* pipe closed / process disposed */ }
    }

    /// <summary>Wait for exit up to <paramref name="timeoutMs"/>. Returns false on timeout; rethrows real cancellation.</summary>
    private static async Task<bool> WaitForExitAsync(Process process, int timeoutMs, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            return false;
        }
    }

    /// <summary>
    /// Stable client ID persisted on disk, scoped per --name so aliased instances
    /// on the same machine don't share an id. Mirrors the legacy single-file layout.
    /// </summary>
    internal static string ResolveClientId(string clientName)
    {
        var idDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.Create),
            "RemoteCmd");
        Directory.CreateDirectory(idDir);

        var idFile = Path.Combine(idDir, $"client.{SanitizeForFileName(clientName)}.id");
        var legacyIdFile = Path.Combine(idDir, "client.id");
        if (!File.Exists(idFile) && clientName == Environment.MachineName && File.Exists(legacyIdFile))
        {
            try { File.Copy(legacyIdFile, idFile); } catch { /* best-effort */ }
        }

        if (File.Exists(idFile))
        {
            var existing = File.ReadAllText(idFile).Trim();
            if (existing.Length >= 8) return existing;
        }

        var id = Guid.NewGuid().ToString("N");
        File.WriteAllText(idFile, id);
        return id;
    }

    private static string SanitizeForFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        var sanitized = sb.ToString().Trim('.', ' ');
        return string.IsNullOrEmpty(sanitized) ? "default" : sanitized;
    }
}
