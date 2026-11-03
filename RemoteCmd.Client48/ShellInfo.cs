using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace RemoteCmd.Client48
{
    /// <summary>
    /// The command shells present on this machine and the one commands run in. Windows PowerShell is
    /// preferred; systems without it (ReactOS-based ones) fall back to cmd.exe.
    /// </summary>
    internal static class ShellInfo
    {
        internal sealed class Shell
        {
            public string Name;
            public string Version;
        }

        private static List<Shell> found;

        internal static IReadOnlyList<Shell> All
        {
            get { return found ?? (found = Detect()); }
        }

        /// <summary>True when powershell.exe, the shell commands run in, is installed.</summary>
        internal static bool HasPowerShell
        {
            get { return All.Any(s => s.Name == "powershell"); }
        }

        /// <summary>Name of the shell commands run in: powershell or cmd.</summary>
        internal static string Exec
        {
            get { return HasPowerShell ? "powershell" : "cmd"; }
        }

        /// <summary>"powershell 5.1.26100.1|cmd 10.0.26100.1" for the relay's dashboard.</summary>
        internal static string Summary()
        {
            return string.Join("|", All.Select(s => s.Version.Length > 0 ? s.Name + " " + s.Version : s.Name));
        }

        private static List<Shell> Detect()
        {
            var list = new List<Shell>();
            try
            {
                var system = Environment.SystemDirectory;
                var powershell = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
                if (File.Exists(powershell))
                    list.Add(new Shell { Name = "powershell", Version = WindowsPowerShellVersion() ?? FileVersion(powershell) });

                var pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
                if (File.Exists(pwsh)) list.Add(new Shell { Name = "pwsh", Version = FileVersion(pwsh) });

                var cmd = Path.Combine(system, "cmd.exe");
                if (File.Exists(cmd)) list.Add(new Shell { Name = "cmd", Version = FileVersion(cmd) });
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
            return space < 0 ? version : version.Substring(0, space);
        }

        // powershell.exe carries the Windows build number; the language version people mean (5.1) is registered here.
        private static string WindowsPowerShellVersion()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\PowerShell\3\PowerShellEngine"))
                    return key == null ? null : key.GetValue("PowerShellVersion") as string;
            }
            catch
            {
                return null;
            }
        }
    }
}
