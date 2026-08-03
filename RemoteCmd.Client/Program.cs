using System.Runtime.InteropServices;
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

if (args[0] is "--version" or "-v" or "version")
{
    Console.WriteLine($"RemoteCmd.Client {ClientStats.Version}");
    return 0;
}

switch (args[0])
{
    case "install-service":
    {
        var (config, serviceName, _) = ParseServiceArgs(args, requireConnection: true);
        if (config is null) return 1;
        return ServiceInstaller.Install(serviceName, config);
    }
    case "uninstall-service":
    {
        var (_, serviceName, _) = ParseServiceArgs(args, requireConnection: false);
        return ServiceInstaller.Uninstall(serviceName);
    }
    default:
    {
        var (config, _, dashboard) = ParseServiceArgs(args, requireConnection: true);
        if (config is null) { PrintUsage(); return 1; }
        await RunHost(config, dashboard);
        return 0;
    }
}

static async Task RunHost(ClientConfig config, bool dashboard)
{
    var builder = Host.CreateApplicationBuilder();
    builder.Services.AddSingleton(config);
    builder.Services.AddHostedService<PollWorker>();

    // No-ops unless actually launched by the SCM / systemd, so console runs are unaffected.
    builder.Services.AddWindowsService(o => o.ServiceName = "RemoteCmdClient");
    builder.Services.AddSystemd();

    if (dashboard && !Console.IsOutputRedirected)
    {
        // Split-screen console: a fixed stats header + a scrolling log fed by ILogger.
        var stats = new ClientStats
        {
            ServerUrl = config.BaseUrl,
            Name = config.Name,
            ClientId = PollWorker.ResolveClientId(config.Name),
            TokenMasked = Mask(config.Token),
            Shell = PollWorker.DefaultShell(),
        };
        var ring = new LogRing();
        builder.Services.AddSingleton(stats);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new DashboardLoggerProvider(ring, stats));

        var host = builder.Build();
        ClientDashboard.EnableAnsi();
        ClientDashboard.Enter();

        // A `kill` or a service stop must not leave the terminal stuck in the alternate buffer with
        // a hidden cursor: restore it from the signal handler as well as from the normal exit path.
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => ClientDashboard.Leave());
        using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => ClientDashboard.Leave());
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ClientDashboard.Leave();

        using var repaintCts = new CancellationTokenSource();
        var repaint = Task.Run(async () =>
        {
            while (!repaintCts.IsCancellationRequested)
            {
                var w = SafeWidth();
                var h = SafeHeight();
                Console.Write(ClientDashboard.Render(stats, ring.Snapshot(), w, h, DateTime.UtcNow));
                try { await Task.Delay(500, repaintCts.Token); } catch (OperationCanceledException) { break; }
            }
        });

        try { await host.RunAsync(); }
        finally
        {
            repaintCts.Cancel();
            try { await repaint; } catch { /* ignore */ }
            ClientDashboard.Leave();
        }
        return;
    }

    builder.Logging.AddSimpleConsole(o => o.TimestampFormat = "HH:mm:ss ");
    await builder.Build().RunAsync();
}

static int SafeWidth() { try { return Console.WindowWidth > 0 ? Console.WindowWidth : 100; } catch { return 100; } }
static int SafeHeight() { try { return Console.WindowHeight > 0 ? Console.WindowHeight : 30; } catch { return 30; } }

static string Mask(string token)
    => token.Length <= 4 ? new string('*', token.Length) : token[..2] + new string('*', token.Length - 4) + token[^2..];

// Parse: [command] <server> <token> [--name X] [--service-name N] [--dashboard]
// Returns null config when a connection is required but server/token are missing.
static (ClientConfig? config, string serviceName, bool dashboard) ParseServiceArgs(string[] args, bool requireConnection)
{
    var serviceName = "RemoteCmdClient";
    string? server = null, token = null, name = null;
    var dashboard = false;
    var positional = new List<string>();

    // Skip a leading verb (install-service / uninstall-service).
    var start = args[0] is "install-service" or "uninstall-service" ? 1 : 0;

    for (var i = start; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--service": break; // host-mode marker, no value
            case "--dashboard": dashboard = true; break;
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
        return (null, serviceName, dashboard);
    }

    var config = server is not null && token is not null
        ? new ClientConfig(server, token, name ?? Environment.MachineName)
        : null;
    return (config, serviceName, dashboard);
}

static void PrintUsage()
{
    Console.WriteLine($"RemoteCmd Client {ClientStats.Version}");
    Console.WriteLine();
    Console.WriteLine("Run (console/foreground):");
    Console.WriteLine("  RemoteCmd.Client <server> <token> [--name <alias>] [--dashboard]");
    Console.WriteLine();
    Console.WriteLine("  --dashboard   live status header (version, connection, polls, running) + scrolling log");
    Console.WriteLine("  --version     print version and exit");
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
    Console.WriteLine("  RemoteCmd.Client http://192.168.3.41:7890 mySecretToken --name comos-1 --dashboard");
    Console.WriteLine("  RemoteCmd.Client install-service http://192.168.3.41:7890 mySecretToken --name comos-1");
}
