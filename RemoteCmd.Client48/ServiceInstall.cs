using System;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RemoteCmd.Client48
{
    /// <summary>Registers the exe as a Windows Service via sc.exe (net48 is Windows-only).</summary>
    internal static class ServiceInstall
    {
        public static int Install(string serviceName, ClientConfig config)
        {
            // The service command line is readable by every local user (sc qc), so the token goes
            // into a file only SYSTEM and administrators can open; the service gets its path.
            string configPath;
            try { configPath = WriteConfig(serviceName, config); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Could not save the service configuration: " + ex.Message);
                return 1;
            }

            var exePath = Process.GetCurrentProcess().MainModule.FileName;
            var binPath = "\"" + exePath + "\" --service --config \"" + configPath + "\"";
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
            try { File.Delete(ConfigPath(serviceName)); } catch { /* nothing to remove */ }
            Console.WriteLine(delete == 0
                ? "Service '" + serviceName + "' removed."
                : "Failed to remove service '" + serviceName + "' (code " + delete + ").");
            return delete;
        }

        /// <summary>Reads a configuration written by <see cref="Install"/>; null when the file is missing or incomplete.</summary>
        public static ClientConfig ReadConfig(string path)
        {
            var json = File.ReadAllText(path);
            var server = Json.GetString(json, "server");
            var token = Json.GetString(json, "token");
            if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(token)) return null;
            return new ClientConfig
            {
                ServerArg = server,
                Token = token,
                Name = Json.GetString(json, "name") ?? Environment.MachineName
            };
        }

        private static string ConfigPath(string serviceName)
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "RemoteCmd", "client48-" + serviceName + ".json");

        private static string WriteConfig(string serviceName, ClientConfig config)
        {
            var path = ConfigPath(serviceName);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, Json.Config(config.ServerArg, config.Token, config.Name));
            Restrict(path);
            return path;
        }

        // Only SYSTEM and administrators, without the inherited read access ProgramData gives every user.
        private static void Restrict(string path)
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            File.SetAccessControl(path, security);
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
