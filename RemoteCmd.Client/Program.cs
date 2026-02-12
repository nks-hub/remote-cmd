using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args.Length < 2)
{
    Console.WriteLine("Usage: RemoteCmd.Client.exe <server_ip_or_url> <token>");
    Console.WriteLine("Example: RemoteCmd.Client.exe 185.14.232.90 mySecretToken");
    return;
}

var serverArg = args[0];
var token = args[1];

// Default to HTTPS, support explicit http://
var baseUrl = serverArg.StartsWith("http")
    ? serverArg.TrimEnd('/')
    : $"https://{serverArg}:7890";

var pollUrl = $"{baseUrl}/api/poll?token={token}";
var resultUrl = $"{baseUrl}/api/result?token={token}";
var filePollUrl = $"{baseUrl}/api/file-poll?token={token}";
var fileDataUrl = $"{baseUrl}/api/file-data?token={token}";
var fileDoneUrl = $"{baseUrl}/api/file-done?token={token}";
var fileUploadUrl = $"{baseUrl}/api/file-upload?token={token}";

// Initialize AES-256-GCM encryption from token
Crypto.Init(token);

// Accept self-signed certificates
var handler = new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (_, _, _, _) => true
};
using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };

Console.WriteLine($"=== Remote CMD Client ===");
Console.WriteLine($"Server: {baseUrl}");
Console.WriteLine($"Encryption: AES-256-GCM");
Console.WriteLine($"Polling for commands and file transfers...");
Console.WriteLine();

var retryDelay = 1;

while (true)
{
    try
    {
        // Poll for commands (encrypted)
        var response = await http.GetFromJsonAsync<PollResponse>(pollUrl);
        if (response?.Command != null)
        {
            // Decrypt command
            var command = Crypto.DecryptString(response.Command);
            Console.WriteLine($"[CMD] {command}");
            var (output, exitCode) = await ExecuteCommand(command);
            Console.WriteLine(output);
            if (exitCode != 0) Console.WriteLine($"[EXIT CODE: {exitCode}]");
            Console.WriteLine();

            // Encrypt result and send as raw bytes
            var resultJson = JsonSerializer.Serialize(new CommandResult { Output = output, ExitCode = exitCode });
            var encrypted = Crypto.Encrypt(Encoding.UTF8.GetBytes(resultJson));
            using var content = new ByteArrayContent(encrypted);
            await http.PostAsync(resultUrl, content);
        }

        // Poll for file transfers (encrypted metadata)
        var filePoll = await http.GetFromJsonAsync<EncryptedFilePoll>(filePollUrl);
        if (filePoll?.E != null)
        {
            var metaJson = Crypto.DecryptString(filePoll.E);
            var meta = JsonSerializer.Deserialize<FilePollMeta>(metaJson);

            if (meta?.Action == "upload" && meta.Path != null)
            {
                Console.WriteLine($"[FILE] Receiving {meta.Size / 1024 / 1024}MB → {meta.Path}");
                try
                {
                    // Download encrypted file data, decrypt
                    var encryptedData = await http.GetByteArrayAsync(fileDataUrl);
                    var fileData = Crypto.Decrypt(encryptedData);
                    var dir = Path.GetDirectoryName(meta.Path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    await File.WriteAllBytesAsync(meta.Path, fileData);
                    await http.PostAsync(fileDoneUrl, null);
                    Console.WriteLine($"[FILE] Saved {fileData.Length / 1024 / 1024}MB → {meta.Path}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FILE ERROR] {ex.Message}");
                    await http.PostAsync(fileDoneUrl, null);
                }
            }
            else if (meta?.Action == "download" && meta.Path != null)
            {
                Console.WriteLine($"[FILE] Uploading ← {meta.Path}");
                try
                {
                    if (!File.Exists(meta.Path))
                    {
                        await http.PostAsync($"{fileUploadUrl}&error=File not found: {meta.Path}", null);
                    }
                    else
                    {
                        // Read file, encrypt, send
                        var fileData = await File.ReadAllBytesAsync(meta.Path);
                        var encrypted = Crypto.Encrypt(fileData);
                        using var content = new ByteArrayContent(encrypted);
                        await http.PostAsync(fileUploadUrl, content);
                        Console.WriteLine($"[FILE] Uploaded {fileData.Length / 1024 / 1024}MB ← {meta.Path}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FILE ERROR] {ex.Message}");
                    await http.PostAsync($"{fileUploadUrl}&error={ex.Message}", null);
                }
            }
        }

        retryDelay = 1;
        await Task.Delay(800);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERROR] {ex.Message} - retry in {retryDelay}s");
        await Task.Delay(retryDelay * 1000);
        retryDelay = Math.Min(retryDelay * 2, 30);
    }
}

static async Task<(string output, int exitCode)> ExecuteCommand(string command)
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
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        var exited = process.WaitForExit(60_000);
        if (!exited)
        {
            process.Kill();
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

// === JSON models ===

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
