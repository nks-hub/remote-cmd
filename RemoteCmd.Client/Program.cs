using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

if (args.Length < 2)
{
    Console.WriteLine("Usage: RemoteCmd.Client.exe <server_ip_or_url> <token>");
    Console.WriteLine("Example: RemoteCmd.Client.exe 185.14.232.90 abc123");
    return;
}

var serverArg = args[0];
var token = args[1];

var baseUrl = serverArg.StartsWith("http")
    ? serverArg.TrimEnd('/')
    : $"http://{serverArg}:7890";

var pollUrl = $"{baseUrl}/api/poll?token={token}";
var resultUrl = $"{baseUrl}/api/result?token={token}";
var filePollUrl = $"{baseUrl}/api/file-poll?token={token}";
var fileDataUrl = $"{baseUrl}/api/file-data?token={token}";
var fileDoneUrl = $"{baseUrl}/api/file-done?token={token}";
var fileUploadUrl = $"{baseUrl}/api/file-upload?token={token}";

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

Console.WriteLine($"=== Remote CMD Client ===");
Console.WriteLine($"Server: {baseUrl}");
Console.WriteLine($"Polling for commands and file transfers...");
Console.WriteLine();

var retryDelay = 1;

while (true)
{
    try
    {
        // Poll for commands
        var response = await http.GetFromJsonAsync<PollResponse>(pollUrl);
        if (response?.Command != null)
        {
            Console.WriteLine($"[CMD] {response.Command}");
            var (output, exitCode) = await ExecuteCommand(response.Command);
            Console.WriteLine(output);
            if (exitCode != 0) Console.WriteLine($"[EXIT CODE: {exitCode}]");
            Console.WriteLine();
            await http.PostAsJsonAsync(resultUrl, new CommandResult { Output = output, ExitCode = exitCode });
        }

        // Poll for file transfers
        var filePoll = await http.GetFromJsonAsync<FilePollResponse>(filePollUrl);
        if (filePoll?.Action == "upload" && filePoll.Path != null)
        {
            Console.WriteLine($"[FILE] Receiving {filePoll.Size / 1024 / 1024}MB → {filePoll.Path}");
            try
            {
                var fileData = await http.GetByteArrayAsync(fileDataUrl);
                var dir = Path.GetDirectoryName(filePoll.Path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                await File.WriteAllBytesAsync(filePoll.Path, fileData);
                await http.PostAsync(fileDoneUrl, null);
                Console.WriteLine($"[FILE] Saved {fileData.Length / 1024 / 1024}MB → {filePoll.Path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FILE ERROR] {ex.Message}");
                await http.PostAsync(fileDoneUrl, null);
            }
        }
        else if (filePoll?.Action == "download" && filePoll.Path != null)
        {
            Console.WriteLine($"[FILE] Uploading ← {filePoll.Path}");
            try
            {
                if (!File.Exists(filePoll.Path))
                {
                    await http.PostAsync($"{fileUploadUrl}&error=File not found: {filePoll.Path}", null);
                }
                else
                {
                    var fileData = await File.ReadAllBytesAsync(filePoll.Path);
                    using var content = new ByteArrayContent(fileData);
                    await http.PostAsync(fileUploadUrl, content);
                    Console.WriteLine($"[FILE] Uploaded {fileData.Length / 1024 / 1024}MB ← {filePoll.Path}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FILE ERROR] {ex.Message}");
                await http.PostAsync($"{fileUploadUrl}&error={ex.Message}", null);
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

class PollResponse
{
    [JsonPropertyName("command")]
    public string? Command { get; set; }
}

class FilePollResponse
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
