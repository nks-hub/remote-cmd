using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RemoteCmd.Client;

/// <summary>
/// Registers/unregisters the client as a system service. Windows uses sc.exe
/// (LocalSystem, auto-start, crash recovery); Linux writes a systemd unit.
/// </summary>
public static class ServiceInstaller
{
    public static int Install(string serviceName, ClientConfig config)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot resolve executable path");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return InstallWindows(serviceName, exePath, config);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return InstallSystemd(serviceName, exePath, config);

        Console.Error.WriteLine("Service install is supported on Windows and Linux only.");
        return 2;
    }

    public static int Uninstall(string serviceName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return UninstallWindows(serviceName);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return UninstallSystemd(serviceName);

        Console.Error.WriteLine("Service uninstall is supported on Windows and Linux only.");
        return 2;
    }

    private static string BuildServiceArgs(ClientConfig config)
        => $"--service {Quote(config.ServerArg)} {Quote(config.Token)} --name {Quote(config.Name)}";

    private static int InstallWindows(string serviceName, string exePath, ClientConfig config)
    {
        // sc.exe wants `binPath= "..."` with the space after '=' and the whole
        // value (exe + args) double-quoted, so inner quotes are doubled.
        var binPath = $"\"{exePath}\" {BuildServiceArgs(config)}";
        var escaped = binPath.Replace("\"", "\\\"");

        var create = RunSc($"create {serviceName} binPath= \"{escaped}\" start= auto obj= LocalSystem DisplayName= \"RemoteCmd Client ({config.Name})\"");
        if (create != 0) return create;

        RunSc($"description {serviceName} \"RemoteCmd polling client ({config.Name})\"");
        RunSc($"failure {serviceName} reset= 86400 actions= restart/5000/restart/10000/restart/15000");
        var start = RunSc($"start {serviceName}");

        Console.WriteLine(start == 0
            ? $"Service '{serviceName}' installed and started."
            : $"Service '{serviceName}' installed (start returned {start}; check privileges).");
        return 0;
    }

    private static int UninstallWindows(string serviceName)
    {
        RunSc($"stop {serviceName}");
        var delete = RunSc($"delete {serviceName}");
        Console.WriteLine(delete == 0
            ? $"Service '{serviceName}' removed."
            : $"Failed to remove service '{serviceName}' (code {delete}).");
        return delete;
    }

    private static int RunSc(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (!string.IsNullOrWhiteSpace(stdout)) Console.Write(stdout);
            if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.Write(stderr);
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"sc.exe failed: {ex.Message}");
            return 1;
        }
    }

    private static int InstallSystemd(string serviceName, string exePath, ClientConfig config)
    {
        var unitPath = $"/etc/systemd/system/{serviceName}.service";
        var unit = new StringBuilder()
            .AppendLine("[Unit]")
            .AppendLine($"Description=RemoteCmd polling client ({config.Name})")
            .AppendLine("After=network-online.target")
            .AppendLine("Wants=network-online.target")
            .AppendLine()
            .AppendLine("[Service]")
            .AppendLine("Type=notify")
            .AppendLine($"ExecStart={Quote(exePath)} {BuildServiceArgs(config)}")
            .AppendLine("Restart=always")
            .AppendLine("RestartSec=5")
            .AppendLine()
            .AppendLine("[Install]")
            .AppendLine("WantedBy=multi-user.target")
            .ToString();

        try
        {
            File.WriteAllText(unitPath, unit);
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Permission denied writing {unitPath}. Run as root (sudo).");
            return 13;
        }

        RunShell("systemctl", "daemon-reload");
        var enable = RunShell("systemctl", $"enable --now {serviceName}");
        Console.WriteLine(enable == 0
            ? $"systemd unit '{serviceName}' installed and started."
            : $"systemd unit written to {unitPath}; 'systemctl enable --now' returned {enable}.");
        return 0;
    }

    private static int UninstallSystemd(string serviceName)
    {
        RunShell("systemctl", $"disable --now {serviceName}");
        var unitPath = $"/etc/systemd/system/{serviceName}.service";
        try { if (File.Exists(unitPath)) File.Delete(unitPath); }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Permission denied removing {unitPath}. Run as root (sudo).");
            return 13;
        }
        RunShell("systemctl", "daemon-reload");
        Console.WriteLine($"systemd unit '{serviceName}' removed.");
        return 0;
    }

    private static int RunShell(string file, string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments,
                UseShellExecute = false
            })!;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{file} failed: {ex.Message}");
            return 1;
        }
    }

    private static string Quote(string value) => $"\"{value}\"";
}
