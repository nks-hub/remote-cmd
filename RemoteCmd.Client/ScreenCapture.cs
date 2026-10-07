using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RemoteCmd.Client;

/// <summary>
/// Captures the screen to a PNG file and returns its path. Each platform is served by the tool it
/// already ships with, so the client gains no dependency and no native interop:
/// PowerShell + System.Drawing on Windows, screencapture on macOS, and whichever of grim, spectacle,
/// gnome-screenshot, scrot or ImageMagick's import is installed on Linux.
/// </summary>
public static class ScreenCapture
{
    public sealed record Result(bool Ok, string? Path, string Message);

    /// <summary>
    /// A service runs in session 0 on Windows, which has no desktop to photograph: the capture
    /// succeeds but the image is black. Callers surface this so the blank result is explainable.
    /// </summary>
    public static bool LikelyHeadlessSession =>
        OperatingSystem.IsWindows() && Process.GetCurrentProcess().SessionId == 0;

    public static async Task<Result> CaptureAsync(string? destination, CancellationToken ct)
    {
        var path = string.IsNullOrWhiteSpace(destination)
            ? Path.Combine(Path.GetTempPath(), $"rcmd-screen-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.png")
            : destination!;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (string.IsNullOrWhiteSpace(destination)) PruneOldCaptures();

        try
        {
            if (OperatingSystem.IsWindows()) await CaptureWindowsAsync(path, ct);
            else if (OperatingSystem.IsMacOS()) await CaptureMacAsync(path, ct);
            else await CaptureLinuxAsync(path, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new Result(false, null, ex.Message);
        }

        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            return new Result(false, null, "capture produced no image");

        var note = LikelyHeadlessSession
            ? " (warning: running in session 0, the image is probably blank - run the client in the interactive session to capture a real desktop)"
            : "";
        return new Result(true, path, $"{new FileInfo(path).Length} bytes{note}");
    }

    /// <summary>
    /// Drops captures this class left in temp more than an hour ago, so a caller that takes
    /// screenshots repeatedly does not slowly fill the disk. Never touches a caller-chosen path.
    /// </summary>
    static void PruneOldCaptures()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (var old in Directory.EnumerateFiles(Path.GetTempPath(), "rcmd-screen-*.png"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(old) < cutoff) File.Delete(old);
                }
                catch { /* in use or not ours - leave it */ }
            }
        }
        catch { /* temp unreadable; pruning is never worth failing a capture over */ }
    }

    static async Task CaptureWindowsAsync(string path, CancellationToken ct)
    {
        // Virtual screen covers every monitor, including ones left of or above the primary.
        var script = $$"""
            Add-Type -AssemblyName System.Drawing,System.Windows.Forms
            $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
            $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
            $bmp.Save('{{path.Replace("'", "''")}}', [System.Drawing.Imaging.ImageFormat]::Png)
            $g.Dispose(); $bmp.Dispose()
            """;

        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        await RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], ct);
    }

    static Task CaptureMacAsync(string path, CancellationToken ct) =>
        // -x silences the shutter sound, which is rude on someone's own laptop.
        RunAsync("/usr/sbin/screencapture", ["-x", "-t", "png", path], ct);

    static async Task CaptureLinuxAsync(string path, CancellationToken ct)
    {
        var wayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        (string exe, string[] args)[] candidates = wayland
            ? [("grim", [path]), ("spectacle", ["-b", "-n", "-o", path]), ("gnome-screenshot", ["-f", path])]
            : [("scrot", ["-o", path]), ("import", ["-window", "root", path]), ("gnome-screenshot", ["-f", path]), ("grim", [path])];

        var tried = new List<string>();
        foreach (var (exe, args) in candidates)
        {
            if (Which(exe) is null) { tried.Add(exe); continue; }
            await RunAsync(exe, args, ct);
            if (File.Exists(path) && new FileInfo(path).Length > 0) return;
            tried.Add(exe);
        }
        throw new InvalidOperationException(
            $"no screenshot tool found (tried {string.Join(", ", tried)}); install grim, scrot or imagemagick");
    }

    static string? Which(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var full = Path.Combine(dir, exe);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    static async Task RunAsync(string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"cannot start {exe}");
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"{exe} exited {proc.ExitCode}: {stderr.Trim()}");
    }
}
