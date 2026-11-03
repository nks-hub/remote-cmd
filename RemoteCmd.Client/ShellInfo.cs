using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace RemoteCmd.Client;

/// <summary>
/// The command shells present on this machine and the one commands run in. Windows PowerShell is
/// preferred; systems without it (ReactOS-based ones, Nano Server) fall back to cmd.exe.
/// </summary>
public static class ShellInfo
{
    public sealed record Shell(string Name, string Version);

    private static readonly Lazy<IReadOnlyList<Shell>> Found = new(Detect);

    public static IReadOnlyList<Shell> All => Found.Value;

    /// <summary>True when powershell.exe, the shell commands run in on Windows, is installed.</summary>
    public static bool HasPowerShell => All.Any(s => s.Name == "powershell");

    /// <summary>Name of the shell commands run in: powershell, cmd or bash.</summary>
    public static string Exec => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? HasPowerShell ? "powershell" : "cmd"
        : "bash";

    /// <summary>"powershell 5.1.26100.1|cmd 10.0.26100.1" for the relay's dashboard.</summary>
    public static string Summary()
        => string.Join('|', All.Select(s => s.Version.Length > 0 ? $"{s.Name} {s.Version}" : s.Name));

    private static IReadOnlyList<Shell> Detect()
    {
        var list = new List<Shell>();
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var system = Environment.SystemDirectory;
                var powershell = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
                if (File.Exists(powershell)) list.Add(new Shell("powershell", WindowsPowerShellVersion() ?? FileVersion(powershell)));

                var pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
                if (File.Exists(pwsh)) list.Add(new Shell("pwsh", FileVersion(pwsh)));

                var cmd = Path.Combine(system, "cmd.exe");
                if (File.Exists(cmd)) list.Add(new Shell("cmd", FileVersion(cmd)));
            }
            else if (File.Exists("/bin/bash"))
            {
                list.Add(new Shell("bash", BashVersion()));
            }
        }
        catch
        {
            // Reporting shells is informational; never let it stop the client.
        }
        return list;
    }

    private static string FileVersion(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        var version = info.ProductVersion ?? info.FileVersion ?? "";
        var space = version.IndexOf(' ');
        return space < 0 ? version : version[..space];
    }

    // powershell.exe carries the Windows build number; the language version people mean (5.1) is registered here.
    private static string? WindowsPowerShellVersion()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\PowerShell\3\PowerShellEngine");
        return key?.GetValue("PowerShellVersion") as string;
    }

    private static string BashVersion()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("/bin/bash", "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            if (process is null) return "";
            var firstLine = process.StandardOutput.ReadLine() ?? "";
            if (!process.WaitForExit(3000)) { try { process.Kill(); } catch { /* ignore */ } }
            return Regex.Match(firstLine, @"version\s+(\S+?)(\(|$|\s)").Groups[1].Value;
        }
        catch
        {
            return "";
        }
    }
}
