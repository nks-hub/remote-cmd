using System;
using System.Collections.Generic;
using System.ServiceProcess;
using System.Threading;

namespace RemoteCmd.Client48
{
    internal static class Program
    {
        private const string DefaultServiceName = "RemoteCmdClient";

        private static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] == "-h" || args[0] == "--help" || args[0] == "/?")
            {
                PrintUsage();
                return args.Length == 0 ? 1 : 0;
            }

            switch (args[0])
            {
                case "install-service":
                {
                    string serviceName;
                    var config = Parse(args, true, out serviceName);
                    if (config == null) return 1;
                    return ServiceInstall.Install(serviceName, config);
                }
                case "uninstall-service":
                {
                    string serviceName;
                    Parse(args, false, out serviceName);
                    return ServiceInstall.Uninstall(serviceName);
                }
                case "--service":
                {
                    string serviceName;
                    var config = Parse(args, true, out serviceName);
                    if (config == null) return 1;
                    ClientService.RunAsService(config);
                    return 0;
                }
                default:
                {
                    string serviceName;
                    var config = Parse(args, true, out serviceName);
                    if (config == null) { PrintUsage(); return 1; }
                    return RunConsole(config);
                }
            }
        }

        private static int RunConsole(ClientConfig config)
        {
            using (var cts = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    Console.WriteLine("[SHUTDOWN] Ctrl+C received, stopping...");
                    cts.Cancel();
                };
                PollLoop.Run(config, Console.WriteLine, cts.Token);
            }
            return 0;
        }

        // Parse: [verb] <server> <token> [--name X] [--service-name N]
        private static ClientConfig Parse(string[] args, bool requireConnection, out string serviceName)
        {
            serviceName = DefaultServiceName;
            string name = null;
            var positional = new List<string>();

            int start = (args[0] == "install-service" || args[0] == "uninstall-service" || args[0] == "--service") ? 1 : 0;
            for (int i = start; i < args.Length; i++)
            {
                if (args[i] == "--name" && i + 1 < args.Length) { name = args[++i]; }
                else if (args[i] == "--service-name" && i + 1 < args.Length) { serviceName = args[++i]; }
                else positional.Add(args[i]);
            }

            string server = positional.Count >= 1 ? positional[0] : null;
            string token = positional.Count >= 2 ? positional[1] : null;

            if (requireConnection && (server == null || token == null))
            {
                Console.Error.WriteLine("Error: <server> and <token> are required.");
                return null;
            }
            if (server == null || token == null) return null;

            return new ClientConfig
            {
                ServerArg = server,
                Token = token,
                Name = name ?? Environment.MachineName
            };
        }

        private static void PrintUsage()
        {
            Console.WriteLine("RemoteCmd Client48 (.NET Framework 4.8 / Windows 7+)");
            Console.WriteLine();
            Console.WriteLine("Run (console):");
            Console.WriteLine("  RemoteCmd.Client48.exe <server> <token> [--name <alias>]");
            Console.WriteLine();
            Console.WriteLine("Run as service host (used internally by SCM):");
            Console.WriteLine("  RemoteCmd.Client48.exe --service <server> <token> [--name <alias>]");
            Console.WriteLine();
            Console.WriteLine("Register as Windows Service (needs admin):");
            Console.WriteLine("  RemoteCmd.Client48.exe install-service <server> <token> [--name <alias>] [--service-name <name>]");
            Console.WriteLine("  RemoteCmd.Client48.exe uninstall-service [--service-name <name>]");
            Console.WriteLine();
            Console.WriteLine("Default service name: " + DefaultServiceName);
        }
    }
}
