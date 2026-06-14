using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using RemoteCmd.Client;

const string defaultServiceName = "RemoteCmdClient";

if (args.Length == 0 || args[0] is "-h" or "--help" or "/?")
{
    PrintUsage();
    return args.Length == 0 ? 1 : 0;
}

switch (args[0])
{
    case "install-service":
    {
        var (config, serviceName) = ParseServiceArgs(args, requireConnection: true);
        if (config is null) return 1;
        return ServiceInstaller.Install(serviceName, config);
    }
    case "uninstall-service":
    {
        var (_, serviceName) = ParseServiceArgs(args, requireConnection: false);
        return ServiceInstaller.Uninstall(serviceName);
    }
    default:
    {
        var (config, _) = ParseServiceArgs(args, requireConnection: true);
        if (config is null) { PrintUsage(); return 1; }
        await RunHost(config);
        return 0;
    }
}

static async Task RunHost(ClientConfig config)
{
    var builder = Host.CreateApplicationBuilder();
    builder.Services.AddSingleton(config);
    builder.Services.AddHostedService<PollWorker>();

    // No-ops unless actually launched by the SCM / systemd, so console runs are unaffected.
    // AddWindowsService wires up the EventLog logger when running under the SCM;
    // under systemd journald captures stdout from the console logger.
    builder.Services.AddWindowsService(o => o.ServiceName = "RemoteCmdClient");
    builder.Services.AddSystemd();
    builder.Logging.AddSimpleConsole(o => o.TimestampFormat = "HH:mm:ss ");

    await builder.Build().RunAsync();
}

// Parse: [command] <server> <token> [--name X] [--service-name N]
// Returns null config when a connection is required but server/token are missing.
static (ClientConfig? config, string serviceName) ParseServiceArgs(string[] args, bool requireConnection)
{
    var serviceName = "RemoteCmdClient";
    string? server = null, token = null, name = null;
    var positional = new List<string>();

    // Skip a leading verb (install-service / uninstall-service / --service).
    var start = args[0] is "install-service" or "uninstall-service" ? 1 : 0;

    for (var i = start; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--service": break; // host-mode marker, no value
            case "--name" when i + 1 < args.Length: name = args[++i]; break;
            case "--service-name" when i + 1 < args.Length: serviceName = args[++i]; break;
            default: positional.Add(args[i]); break;
        }
    }

    if (positional.Count >= 1) server = positional[0];
    if (positional.Count >= 2) token = positional[1];

    if (requireConnection && (server is null || token is null))
    {
        Console.Error.WriteLine("Error: <server> and <token> are required.");
        return (null, serviceName);
    }

    var config = server is not null && token is not null
        ? new ClientConfig(server, token, name ?? Environment.MachineName)
        : null;
    return (config, serviceName);
}

static void PrintUsage()
{
    Console.WriteLine("RemoteCmd Client");
    Console.WriteLine();
    Console.WriteLine("Run (console/foreground):");
    Console.WriteLine("  RemoteCmd.Client <server> <token> [--name <alias>]");
    Console.WriteLine();
    Console.WriteLine("Run as a service host (used internally by SCM/systemd):");
    Console.WriteLine("  RemoteCmd.Client --service <server> <token> [--name <alias>]");
    Console.WriteLine();
    Console.WriteLine("Register as a system service (Windows Service / systemd; needs admin/root):");
    Console.WriteLine("  RemoteCmd.Client install-service <server> <token> [--name <alias>] [--service-name <name>]");
    Console.WriteLine("  RemoteCmd.Client uninstall-service [--service-name <name>]");
    Console.WriteLine();
    Console.WriteLine($"Default service name: {defaultServiceName}");
    Console.WriteLine("Examples:");
    Console.WriteLine("  RemoteCmd.Client 192.168.3.41:7890 mySecretToken --name comos-1");
    Console.WriteLine("  RemoteCmd.Client install-service http://192.168.3.41:7890 mySecretToken --name comos-1");
}
