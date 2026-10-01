using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace RemoteCmd.Shared;

/// <summary>What to register: the executable, its arguments and how the service is named.</summary>
public sealed record ServiceSpec(string Name, string Description, string ExePath, IReadOnlyList<string> Args);

/// <summary>
/// Registers a program as a system service: a Windows service (LocalSystem, auto-start, restart on
/// crash), a systemd unit, or a launchd daemon on macOS. Arguments must not carry secrets — they
/// end up in the service definition, which other users can read; secrets belong in the config file.
/// </summary>
public static class ServiceInstaller
{
    public static int Install(ServiceSpec spec)
    {
        if (OperatingSystem.IsWindows()) return InstallWindows(spec);
        if (OperatingSystem.IsLinux()) return InstallSystemd(spec);
        if (OperatingSystem.IsMacOS()) return InstallLaunchd(spec);
        Console.Error.WriteLine("Service install is supported on Windows, Linux and macOS.");
        return 2;
    }

    public static int Uninstall(string name)
    {
        if (OperatingSystem.IsWindows()) return UninstallWindows(name);
        if (OperatingSystem.IsLinux()) return UninstallSystemd(name);
        if (OperatingSystem.IsMacOS()) return UninstallLaunchd(name);
        Console.Error.WriteLine("Service uninstall is supported on Windows, Linux and macOS.");
        return 2;
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static string CommandLine(ServiceSpec spec)
        => string.Join(' ', new[] { spec.ExePath }.Concat(spec.Args).Select(Quote));

    // --- Windows ---

    private static int InstallWindows(ServiceSpec spec)
    {
        // An existing service of that name is replaced, so re-running an install updates it in place.
        if (Run("sc.exe", $"query {spec.Name}", quiet: true) == 0) UninstallWindows(spec.Name);

        // sc.exe wants `binPath= "..."` with the whole command line (exe + args) as one quoted value.
        var binPath = CommandLine(spec).Replace("\"", "\\\"");
        var create = Run("sc.exe", $"create {spec.Name} binPath= \"{binPath}\" start= auto obj= LocalSystem DisplayName= \"{spec.Description}\"");
        if (create != 0) return create;

        Run("sc.exe", $"description {spec.Name} \"{spec.Description}\"");
        Run("sc.exe", $"failure {spec.Name} reset= 86400 actions= restart/5000/restart/10000/restart/15000");
        var start = Run("sc.exe", $"start {spec.Name}");
        Console.WriteLine(start == 0
            ? $"Service '{spec.Name}' installed and started."
            : $"Service '{spec.Name}' installed (start returned {start}; check privileges).");
        return 0;
    }

    private static int UninstallWindows(string name)
    {
        Run("sc.exe", $"stop {name}", quiet: true);
        // A stopping service cannot be deleted cleanly; give it a moment to let go.
        for (var i = 0; i < 20 && Run("sc.exe", $"query {name}", quiet: true, contains: "STOPPED") != 0; i++) Thread.Sleep(250);
        var delete = Run("sc.exe", $"delete {name}");
        Console.WriteLine(delete == 0 ? $"Service '{name}' removed." : $"Failed to remove service '{name}' (code {delete}).");
        return delete;
    }

    // --- Linux ---

    private static int InstallSystemd(ServiceSpec spec)
    {
        var unitPath = $"/etc/systemd/system/{spec.Name}.service";
        var unit = new StringBuilder()
            .AppendLine("[Unit]")
            .AppendLine($"Description={spec.Description}")
            .AppendLine("After=network-online.target")
            .AppendLine("Wants=network-online.target")
            .AppendLine()
            .AppendLine("[Service]")
            .AppendLine("Type=notify")
            .AppendLine($"ExecStart={CommandLine(spec)}")
            .AppendLine("Restart=always")
            .AppendLine("RestartSec=5")
            .AppendLine()
            .AppendLine("[Install]")
            .AppendLine("WantedBy=multi-user.target")
            .ToString();

        try { File.WriteAllText(unitPath, unit); }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Permission denied writing {unitPath}. Run as root (sudo).");
            return 13;
        }

        Run("systemctl", "daemon-reload");
        var enable = Run("systemctl", $"enable --now {spec.Name}");
        // enable --now does not restart a unit that was already running the old binary.
        Run("systemctl", $"restart {spec.Name}");
        Console.WriteLine(enable == 0
            ? $"systemd unit '{spec.Name}' installed and started."
            : $"systemd unit written to {unitPath}; 'systemctl enable --now' returned {enable}.");
        return 0;
    }

    private static int UninstallSystemd(string name)
    {
        Run("systemctl", $"disable --now {name}");
        var unitPath = $"/etc/systemd/system/{name}.service";
        try { if (File.Exists(unitPath)) File.Delete(unitPath); }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Permission denied removing {unitPath}. Run as root (sudo).");
            return 13;
        }
        Run("systemctl", "daemon-reload");
        Console.WriteLine($"systemd unit '{name}' removed.");
        return 0;
    }

    // --- macOS ---

    private static string LaunchdLabel(string name) => $"cz.nks.remotecmd.{name}";
    private static string PlistPath(string name) => $"/Library/LaunchDaemons/{LaunchdLabel(name)}.plist";

    private static int InstallLaunchd(ServiceSpec spec)
    {
        var label = LaunchdLabel(spec.Name);
        var plistPath = PlistPath(spec.Name);
        var args = string.Concat(new[] { spec.ExePath }.Concat(spec.Args)
            .Select(a => $"\n    <string>{SecurityElement.Escape(a)}</string>"));
        var plist = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{label}</string>
              <key>ProgramArguments</key>
              <array>{args}
              </array>
              <key>RunAtLoad</key><true/>
              <key>KeepAlive</key><true/>
              <key>StandardOutPath</key><string>/var/log/{spec.Name}.log</string>
              <key>StandardErrorPath</key><string>/var/log/{spec.Name}.log</string>
            </dict>
            </plist>
            """;

        try { File.WriteAllText(plistPath, plist); }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Permission denied writing {plistPath}. Run as root (sudo).");
            return 13;
        }

        Run("launchctl", $"bootout system/{label}", quiet: true);
        var load = Run("launchctl", $"bootstrap system {plistPath}");
        Console.WriteLine(load == 0
            ? $"launchd daemon '{label}' installed and started."
            : $"launchd plist written to {plistPath}; 'launchctl bootstrap' returned {load}.");
        return load;
    }

    private static int UninstallLaunchd(string name)
    {
        Run("launchctl", $"bootout system/{LaunchdLabel(name)}", quiet: true);
        try { if (File.Exists(PlistPath(name))) File.Delete(PlistPath(name)); }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Permission denied removing {PlistPath(name)}. Run as root (sudo).");
            return 13;
        }
        Console.WriteLine($"launchd daemon '{LaunchdLabel(name)}' removed.");
        return 0;
    }

    /// <summary>
    /// Runs a tool and returns its exit code. With <paramref name="contains"/>, returns 0 only when the
    /// output contains that text — sc.exe reports a service's state on stdout, not in its exit code.
    /// </summary>
    private static int Run(string file, string arguments, bool quiet = false, string? contains = null)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (!quiet)
            {
                if (!string.IsNullOrWhiteSpace(stdout)) Console.Write(stdout);
                if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.Write(stderr);
            }
            if (contains != null) return stdout.Contains(contains, StringComparison.Ordinal) ? 0 : 1;
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            if (!quiet) Console.Error.WriteLine($"{file} failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>True when this process can install services: elevated on Windows, root elsewhere.</summary>
    public static bool IsPrivileged()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        return Environment.UserName == "root" || geteuid() == 0;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();
}
