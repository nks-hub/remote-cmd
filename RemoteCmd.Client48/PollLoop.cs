using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace RemoteCmd.Client48
{
    internal sealed class ClientConfig
    {
        public string ServerArg;
        public string Token;
        public string Name;

        public string BaseUrl
            => ServerArg.StartsWith("http") ? ServerArg.TrimEnd('/') : "https://" + ServerArg;
    }

    /// <summary>
    /// Polling client for .NET Framework 4.8 (Windows 7+). Wire-compatible with the
    /// .NET 9 relay: command exec via PowerShell plus 200MB file upload/download.
    /// </summary>
    internal static class PollLoop
    {
        public static void Run(ClientConfig config, Action<string> log, CancellationToken ct)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            var baseUrl = config.BaseUrl;
            var clientId = ResolveClientId(config.Name);
            // Token travels in a header — query strings land in access and proxy logs.
            var qs = "?clientId=" + clientId + "&name=" + Uri.EscapeDataString(config.Name);

            var pollUrl = baseUrl + "/api/poll" + qs;
            var resultUrl = baseUrl + "/api/result" + qs;
            var filePollUrl = baseUrl + "/api/file-poll" + qs;
            var fileDataUrl = baseUrl + "/api/file-data" + qs;
            var fileDoneUrl = baseUrl + "/api/file-done" + qs;
            var fileUploadUrl = baseUrl + "/api/file-upload" + qs;

            Crypto.Init(config.Token);

            // Accept the relay's self-signed certificate on this client only. The old
            // ServicePointManager callback turned certificate checking off process-wide.
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) => true
            };
            using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) })
            {
                http.DefaultRequestHeaders.Add("X-Token", config.Token);
                log("Remote CMD Client48 started. Server=" + baseUrl + " Name=" + config.Name + " Id=" + clientId);

                int retryDelay = 1;
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var pollJson = http.GetStringAsync(pollUrl).GetAwaiter().GetResult();
                        var encCommand = Json.GetString(pollJson, "command");
                        if (!string.IsNullOrEmpty(encCommand))
                        {
                            var command = Crypto.DecryptString(encCommand);
                            log("[CMD] " + command);
                            int exitCode;
                            var output = ExecuteCommand(command, out exitCode);

                            var resultJson = Json.Result(output, exitCode);
                            var encrypted = Crypto.Encrypt(Encoding.UTF8.GetBytes(resultJson));
                            using (var content = new ByteArrayContent(encrypted))
                                http.PostAsync(resultUrl, content).GetAwaiter().GetResult();
                        }

                        var filePollJson = http.GetStringAsync(filePollUrl).GetAwaiter().GetResult();
                        var encMeta = Json.GetString(filePollJson, "e");
                        if (!string.IsNullOrEmpty(encMeta))
                        {
                            var metaJson = Crypto.DecryptString(encMeta);
                            var action = Json.GetString(metaJson, "action");
                            var path = Json.GetString(metaJson, "path");
                            var size = Json.GetLong(metaJson, "size", 0);

                            if (action == "upload" && !string.IsNullOrEmpty(path))
                                ReceiveFile(http, path, size, fileDataUrl, fileDoneUrl, log);
                            else if (action == "download" && !string.IsNullOrEmpty(path))
                                SendFile(http, path, fileUploadUrl, log);
                        }

                        retryDelay = 1;
                        WaitOrCancel(800, ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        log("[ERROR] " + ex.Message + " - retry in " + retryDelay + "s");
                        WaitOrCancel(retryDelay * 1000, ct);
                        retryDelay = Math.Min(retryDelay * 2, 30);
                    }
                }
                log("Remote CMD Client48 stopped.");
            }
        }

        private static void ReceiveFile(HttpClient http, string path, long size, string fileDataUrl, string fileDoneUrl, Action<string> log)
        {
            log("[FILE] Receiving " + (size / 1024 / 1024) + "MB -> " + path);
            try
            {
                var encryptedData = http.GetByteArrayAsync(fileDataUrl).GetAwaiter().GetResult();
                var fileData = Crypto.Decrypt(encryptedData);
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, fileData);
                http.PostAsync(fileDoneUrl, null).GetAwaiter().GetResult();
                log("[FILE] Saved " + (fileData.Length / 1024 / 1024) + "MB -> " + path);
            }
            catch (Exception ex)
            {
                log("[FILE ERROR] " + ex.Message);
                http.PostAsync(fileDoneUrl, null).GetAwaiter().GetResult();
            }
        }

        private static void SendFile(HttpClient http, string path, string fileUploadUrl, Action<string> log)
        {
            log("[FILE] Uploading <- " + path);
            try
            {
                if (!File.Exists(path))
                {
                    http.PostAsync(fileUploadUrl + "&error=" + Uri.EscapeDataString("File not found: " + path), null).GetAwaiter().GetResult();
                    return;
                }
                var fileData = File.ReadAllBytes(path);
                var encrypted = Crypto.Encrypt(fileData);
                using (var content = new ByteArrayContent(encrypted))
                    http.PostAsync(fileUploadUrl, content).GetAwaiter().GetResult();
                log("[FILE] Uploaded " + (fileData.Length / 1024 / 1024) + "MB <- " + path);
            }
            catch (Exception ex)
            {
                log("[FILE ERROR] " + ex.Message);
                http.PostAsync(fileUploadUrl + "&error=" + Uri.EscapeDataString(ex.Message), null).GetAwaiter().GetResult();
            }
        }

        private static string ExecuteCommand(string command, out int exitCode)
        {
            try
            {
                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = "-NoProfile -NonInteractive -Command \"" + command.Replace("\"", "\\\"") + "\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    process.Start();
                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();

                    if (!process.WaitForExit(60000))
                    {
                        try { process.Kill(); } catch { }
                        exitCode = -1;
                        return "[KILLED] Command exceeded 60s timeout";
                    }

                    var stdout = outputTask.GetAwaiter().GetResult();
                    var stderr = errorTask.GetAwaiter().GetResult();
                    var combined = stdout;
                    if (!string.IsNullOrWhiteSpace(stderr))
                        combined += "\n[STDERR]\n" + stderr;

                    exitCode = process.ExitCode;
                    return combined.TrimEnd();
                }
            }
            catch (Exception ex)
            {
                exitCode = -1;
                return "[EXEC ERROR] " + ex.Message;
            }
        }

        private static void WaitOrCancel(int ms, CancellationToken ct)
        {
            if (ct.WaitHandle.WaitOne(ms)) throw new OperationCanceledException();
        }

        private static string ResolveClientId(string clientName)
        {
            var idDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RemoteCmd");
            Directory.CreateDirectory(idDir);

            var idFile = Path.Combine(idDir, "client." + SanitizeForFileName(clientName) + ".id");
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
}
