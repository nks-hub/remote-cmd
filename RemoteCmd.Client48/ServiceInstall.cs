using System;
using System.Diagnostics;

namespace RemoteCmd.Client48
{
    /// <summary>Registers the exe as a Windows Service via sc.exe (net48 is Windows-only).</summary>
    internal static class ServiceInstall
    {
        public static int Install(string serviceName, ClientConfig config)
        {
            var exePath = Process.GetCurrentProcess().MainModule.FileName;
            var args = "--service \"" + config.ServerArg + "\" \"" + config.Token + "\" --name \"" + config.Name + "\"";
            var binPath = "\"" + exePath + "\" " + args;
            var escaped = binPath.Replace("\"", "\\\"");

            int create = RunSc("create " + serviceName + " binPath= \"" + escaped + "\" start= auto obj= LocalSystem DisplayName= \"RemoteCmd Client (" + config.Name + ")\"");
            if (create != 0) return create;

            RunSc("description " + serviceName + " \"RemoteCmd polling client (" + config.Name + ")\"");
            RunSc("failure " + serviceName + " reset= 86400 actions= restart/5000/restart/10000/restart/15000");
            int start = RunSc("start " + serviceName);

            Console.WriteLine(start == 0
                ? "Service '" + serviceName + "' installed and started."
                : "Service '" + serviceName + "' installed (start returned " + start + "; check privileges).");
            return 0;
        }

        public static int Uninstall(string serviceName)
        {
            RunSc("stop " + serviceName);
            int delete = RunSc("delete " + serviceName);
            Console.WriteLine(delete == 0
                ? "Service '" + serviceName + "' removed."
                : "Failed to remove service '" + serviceName + "' (code " + delete + ").");
            return delete;
        }

        private static int RunSc(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (!string.IsNullOrEmpty(stdout)) Console.Write(stdout);
                    if (!string.IsNullOrEmpty(stderr)) Console.Error.Write(stderr);
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("sc.exe failed: " + ex.Message);
                return 1;
            }
        }
    }
}
