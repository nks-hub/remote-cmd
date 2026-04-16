using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteCmd.Shared;

if (args.Length < 2)
{
    Console.WriteLine("Usage: RemoteCmd.Client.exe <server_ip_or_url> <token> [--name <alias>]");
    Console.WriteLine("Example: RemoteCmd.Client.exe 185.14.232.90 mySecretToken --name comos-1");
    return 1;
}

var serverArg = args[0];
var token = args[1];

string? clientName = null;
for (int i = 2; i < args.Length - 1; i++)
{
    if (args[i] == "--name") { clientName = args[i + 1]; i++; }
}
clientName ??= Environment.MachineName;

string baseUrl;
if (serverArg.StartsWith("http"))
    baseUrl = serverArg.TrimEnd('/');
else
    baseUrl = $"https://{serverArg}";

// Stable client ID persisted on disk
var idDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
        Environment.SpecialFolderOption.Create),
    "RemoteCmd");
Directory.CreateDirectory(idDir);
var idFile = Path.Combine(idDir, "client.id");
string clientId;
if (File.Exists(idFile))
{
    clientId = File.ReadAllText(idFile).Trim();
    if (string.IsNullOrEmpty(clientId) || clientId.Length < 8)
    {
        clientId = Guid.NewGuid().ToString("N");
        File.WriteAllText(idFile, clientId);
    }
}
else
{
    clientId = Guid.NewGuid().ToString("N");
    File.WriteAllText(idFile, clientId);
}

var encodedName = Uri.EscapeDataString(clientName);
var qs = $"?token={token}&clientId={clientId}&name={encodedName}";

var pollUrl = $"{baseUrl}/api/poll{qs}";
var resultUrl = $"{baseUrl}/api/result{qs}";
var filePollUrl = $"{baseUrl}/api/file-poll{qs}";
var fileDataUrl = $"{baseUrl}/api/file-data{qs}";
var fileDoneUrl = $"{baseUrl}/api/file-done{qs}";
var fileUploadUrl = $"{baseUrl}/api/file-upload{qs}";

Crypto.Init(token);

var handler = new SocketsHttpHandler
{
    SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
    PooledConnectionLifetime = TimeSpan.FromSeconds(30),
    PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
    ConnectTimeout = TimeSpan.FromSeconds(10),
};
using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("[SHUTDOWN] Ctrl+C received, stopping...");
    cts.Cancel();
};

Console.WriteLine("=== Remote CMD Client ===");
Console.WriteLine($"Server: {baseUrl}");
Console.WriteLine($"Client ID: {clientId}");
Console.WriteLine($"Name: {clientName}");
Console.WriteLine($"Encryption: AES-256-GCM");
Console.WriteLine("Polling for commands and file transfers (Ctrl+C to stop)...");
Console.WriteLine();

var retryDelay = 1;
var ct = cts.Token;

while (!ct.IsCancellationRequested)
{
    try
    {
        var response = await http.GetFromJsonAsync<PollResponse>(pollUrl, ct);
        if (response?.Command != null)
        {
            var command = Crypto.DecryptString(response.Command);
            Console.WriteLine($"[CMD] {command}");
            var (output, exitCode) = await ExecuteCommand(command, ct);
            Console.WriteLine(output);
            if (exitCode != 0) Console.WriteLine($"[EXIT CODE: {exitCode}]");
            Console.WriteLine();

            var resultJson = JsonSerializer.Serialize(new CommandResult { Output = output, ExitCode = exitCode });
            var encrypted = Crypto.Encrypt(Encoding.UTF8.GetBytes(resultJson));
            using var content = new ByteArrayContent(encrypted);
            await http.PostAsync(resultUrl, content, ct);
        }

        var filePoll = await http.GetFromJsonAsync<EncryptedFilePoll>(filePollUrl, ct);
        if (filePoll?.E != null)
        {
            var metaJson = Crypto.DecryptString(filePoll.E);
            var meta = JsonSerializer.Deserialize<FilePollMeta>(metaJson);

            if (meta?.Action == "upload" && meta.Path != null)
            {
                Console.WriteLine($"[FILE] Receiving {meta.Size / 1024 / 1024}MB -> {meta.Path}");
                try
                {
                    var encryptedData = await http.GetByteArrayAsync(fileDataUrl, ct);
                    var fileData = Crypto.Decrypt(encryptedData);
                    var dir = Path.GetDirectoryName(meta.Path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    await File.WriteAllBytesAsync(meta.Path, fileData, ct);
                    await http.PostAsync(fileDoneUrl, null, ct);
                    Console.WriteLine($"[FILE] Saved {fileData.Length / 1024 / 1024}MB -> {meta.Path}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.WriteLine($"[FILE ERROR] {ex.Message}");
                    await http.PostAsync(fileDoneUrl, null, ct);
                }
            }
            else if (meta?.Action == "download" && meta.Path != null)
            {
                Console.WriteLine($"[FILE] Uploading <- {meta.Path}");
                try
                {
                    if (!File.Exists(meta.Path))
                    {
                        await http.PostAsync($"{fileUploadUrl}&error={Uri.EscapeDataString("File not found: " + meta.Path)}", null, ct);
                    }
                    else
                    {
                        var fileData = await File.ReadAllBytesAsync(meta.Path, ct);
                        var encrypted = Crypto.Encrypt(fileData);
                        using var content = new ByteArrayContent(encrypted);
                        await http.PostAsync(fileUploadUrl, content, ct);
                        Console.WriteLine($"[FILE] Uploaded {fileData.Length / 1024 / 1024}MB <- {meta.Path}");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.WriteLine($"[FILE ERROR] {ex.Message}");
                    await http.PostAsync($"{fileUploadUrl}&error={Uri.EscapeDataString(ex.Message)}", null, ct);
                }
            }
        }

        retryDelay = 1;
        await Task.Delay(800, ct);
    }
    catch (OperationCanceledException) { break; }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERROR] {ex.Message} - retry in {retryDelay}s");
        try { await Task.Delay(retryDelay * 1000, ct); } catch (OperationCanceledException) { break; }
        retryDelay = Math.Min(retryDelay * 2, 30);
    }
}

Console.WriteLine("[STOPPED]");
return 0;

static async Task<(string output, int exitCode)> ExecuteCommand(string command, CancellationToken ct)
{
    try
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -Command \"{command.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);

        var exited = process.WaitForExit(60_000);
        if (!exited)
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

class PollResponse
{
    [JsonPropertyName("command")]
    public string? Command { get; set; }
}

class EncryptedFilePoll
{
    [JsonPropertyName("e")]
    public string? E { get; set; }
}

class FilePollMeta
{
    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

class CommandResult
{
    [JsonPropertyName("output")]
    public string Output { get; set; } = "";

    [JsonPropertyName("exitCode")]
    public int ExitCode { get; set; }
}
