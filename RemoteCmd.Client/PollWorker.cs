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
    private readonly ClientConfig _config;
    private readonly ILogger<PollWorker> _log;

    public PollWorker(ClientConfig config, ILogger<PollWorker> log)
    {
        _config = config;
        _log = log;
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
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };

        _log.LogInformation("Remote CMD Client started. Server={Server} Name={Name} Id={Id} Shell={Shell}",
            baseUrl, _config.Name, clientId, DefaultShell());

        var retryDelay = 1;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var response = await http.GetFromJsonAsync<PollResponse>(pollUrl, stoppingToken);
                if (response?.Command != null)
                {
                    var command = Crypto.DecryptString(response.Command);
                    _log.LogInformation("[CMD] {Command}", command);
                    var (output, exitCode) = await ExecuteCommand(command, stoppingToken);
                    if (exitCode != 0) _log.LogWarning("[EXIT {Code}]", exitCode);

                    var resultJson = JsonSerializer.Serialize(new CommandResult { Output = output, ExitCode = exitCode });
                    var encrypted = Crypto.Encrypt(Encoding.UTF8.GetBytes(resultJson));
                    using var content = new ByteArrayContent(encrypted);
                    await http.PostAsync(resultUrl, content, stoppingToken);
                }

                var filePoll = await http.GetFromJsonAsync<EncryptedFilePoll>(filePollUrl, stoppingToken);
                if (filePoll?.E != null)
                {
                    var meta = JsonSerializer.Deserialize<FilePollMeta>(Crypto.DecryptString(filePoll.E));
                    if (meta?.Action == "upload" && meta.Path != null)
                        await ReceiveFile(http, meta, fileDataUrl, fileDoneUrl, stoppingToken);
                    else if (meta?.Action == "download" && meta.Path != null)
                        await SendFile(http, meta, fileUploadUrl, stoppingToken);
                }

                retryDelay = 1;
                await Task.Delay(800, stoppingToken);
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

    private static string DefaultShell()
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
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
                : new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    ArgumentList = { "-c", command },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);

            if (!process.WaitForExit(60_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return ("[KILLED] Command exceeded 60s timeout", -1);
            }

            var stdout = await outputTask;
            var stderr = await errorTask;
            var combined = stdout;
            if (!string.IsNullOrWhiteSpace(stderr))
                combined += "\n[STDERR]\n" + stderr;

            return (combined.TrimEnd(), process.ExitCode);
        }
        catch (Exception ex)
        {
            return ($"[EXEC ERROR] {ex.Message}", -1);
        }
    }

    /// <summary>
    /// Stable client ID persisted on disk, scoped per --name so aliased instances
    /// on the same machine don't share an id. Mirrors the legacy single-file layout.
    /// </summary>
    private static string ResolveClientId(string clientName)
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
