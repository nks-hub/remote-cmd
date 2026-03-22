using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args.Length < 2)
{
    Console.WriteLine("Usage: RemoteCmd.Client.exe <server_ip_or_url> <token> [--cert-pin <sha256>]");
    Console.WriteLine("Example: RemoteCmd.Client.exe 185.14.232.90 mySecretToken");
    Console.WriteLine("Example: RemoteCmd.Client.exe 185.14.232.90 mySecretToken --cert-pin AB:CD:EF:...");
    return;
}

var serverArg = args[0];
var token = args[1];

// Parse optional --cert-pin argument
string? certPin = null;
for (int i = 2; i < args.Length - 1; i++)
{
    if (args[i].Equals("--cert-pin", StringComparison.OrdinalIgnoreCase))
    {
        certPin = args[i + 1].Replace(":", "").ToUpperInvariant();
        break;
    }
}

// Default to HTTPS, support explicit http://
var baseUrl = serverArg.StartsWith("http")
    ? serverArg.TrimEnd('/')
    : $"https://{serverArg}:7890";

// Load command policy and path validator
var commandValidator = CommandValidator.Load();
var pathValidator = PathValidator.FromPolicy(commandValidator.Config);

// Build URLs without token (token goes in Authorization header)
var pollUrl = $"{baseUrl}/api/poll";
var resultUrl = $"{baseUrl}/api/result";
var filePollUrl = $"{baseUrl}/api/file-poll";
var fileDataUrl = $"{baseUrl}/api/file-data";
var fileDoneUrl = $"{baseUrl}/api/file-done";
var fileUploadUrl = $"{baseUrl}/api/file-upload";

// Initialize AES-256-GCM encryption from token
Crypto.Init(token);

// TOFU (Trust On First Use) certificate handling
// Store pinned thumbprint in memory for this session; file-based persistence is optional.
string? sessionThumbprint = certPin;

var handler = new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
    {
        if (cert == null) return false;

        var thumbprint = cert.GetCertHashString(HashAlgorithmName.SHA256).ToUpperInvariant();

        // Strict pinning mode: reject if thumbprint does not match
        if (certPin != null)
        {
            var pinNormalized = certPin.Replace(":", "").ToUpperInvariant();
            if (!thumbprint.Equals(pinNormalized, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[TLS] REJECTED: certificate thumbprint mismatch.");
                Console.WriteLine($"[TLS]   Expected : {pinNormalized}");
                Console.WriteLine($"[TLS]   Got      : {thumbprint}");
                return false;
            }
            return true;
        }

        // TOFU mode: accept and warn on first use, enforce on subsequent connections
        if (sessionThumbprint == null)
        {
            Console.WriteLine();
            Console.WriteLine("[TLS] WARNING: Trusting self-signed certificate for this session (TOFU).");
            Console.WriteLine($"[TLS] Certificate thumbprint (SHA-256): {thumbprint}");
            Console.WriteLine("[TLS] To enforce strict pinning, restart with: --cert-pin " + thumbprint);
            Console.WriteLine();
            sessionThumbprint = thumbprint;
            return true;
        }

        if (!thumbprint.Equals(sessionThumbprint, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[TLS] REJECTED: certificate changed since session start (possible MITM).");
            Console.WriteLine($"[TLS]   Session  : {sessionThumbprint}");
            Console.WriteLine($"[TLS]   Got      : {thumbprint}");
            return false;
        }

        return true;
    }
};

using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };

// Set Bearer token in Authorization header for all requests
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

Console.WriteLine($"=== Remote CMD Client ===");
Console.WriteLine($"Server: {baseUrl}");
Console.WriteLine($"Encryption: AES-256-GCM");
Console.WriteLine($"Policy: {commandValidator.Config.Mode}");
if (certPin != null)
    Console.WriteLine($"TLS pin: {certPin}");
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

            // Validate against policy before execution
            var policyError = commandValidator.Validate(command);
            if (policyError != null)
            {
                Console.WriteLine($"[BLOCKED] {policyError}");
                var blockedResult = JsonSerializer.Serialize(new CommandResult
                {
                    Output = $"[BLOCKED] {policyError}",
                    ExitCode = -2
                });
                var blockedEncrypted = Crypto.Encrypt(Encoding.UTF8.GetBytes(blockedResult));
                using var blockedContent = new ByteArrayContent(blockedEncrypted);
                await http.PostAsync(resultUrl, blockedContent);
            }
            else
            {
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

                // Validate destination path before writing
                var pathError = pathValidator.Validate(meta.Path);
                if (pathError != null)
                {
                    Console.WriteLine($"[FILE BLOCKED] {pathError}");
                    await http.PostAsync(fileDoneUrl, null);
                }
                else
                {
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
            }
            else if (meta?.Action == "download" && meta.Path != null)
            {
                Console.WriteLine($"[FILE] Uploading ← {meta.Path}");

                // Validate source path before reading
                var pathError = pathValidator.Validate(meta.Path);
                if (pathError != null)
                {
                    Console.WriteLine($"[FILE BLOCKED] {pathError}");
                    await http.PostAsync($"{fileUploadUrl}?error={Uri.EscapeDataString(pathError)}", null);
                }
                else
                {
                    try
                    {
                        if (!File.Exists(meta.Path))
                        {
                            var notFoundMsg = $"File not found: {meta.Path}";
                            await http.PostAsync($"{fileUploadUrl}?error={Uri.EscapeDataString(notFoundMsg)}", null);
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
                        await http.PostAsync($"{fileUploadUrl}?error={Uri.EscapeDataString(ex.Message)}", null);
                    }
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

        // 3.6: Async WaitForExit with CancellationToken (60s timeout)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 3.5: Kill entire process tree
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
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
