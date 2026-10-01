using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using RemoteCmd.Client;
using RemoteCmd.Shared;

if (args.Length > 0 && args[0] is "-h" or "--help" or "/?")
{
    PrintUsage();
    return 0;
}

if (args.Length > 0 && args[0] is "--version" or "-v" or "version")
{
    Console.WriteLine($"RemoteCmd.Client {ClientStats.Version}");
    return 0;
}

var opts = ClientArgs.Parse(args);
var configPath = opts.ConfigPath ?? ClientConfigFile.DefaultPath(opts.ServiceName);

switch (opts.Verb)
{
    case "pair":
    {
        if (opts.Positional.Count < 2)
        {
            Console.Error.WriteLine("Usage: RemoteCmd.Client pair <server> <code> [--name <alias>] [--install-service]");
            return 1;
        }
        var paired = await PairCommand.RunAsync(opts.Positional[0], opts.Positional[1],
            opts.Name ?? Environment.MachineName, opts.KeepAwake);
        if (paired is null) return 1;
        ClientConfigFile.Save(configPath, paired);
        Console.WriteLine($"Paired as '{paired.Name}' with {paired.BaseUrl}. Settings saved to {configPath}");
        if (paired.CertSha256 is null)
            Console.WriteLine("Note: the relay runs without TLS — traffic is encrypted, but the relay is not authenticated.");
        return opts.InstallService ? InstallService(opts.ServiceName, configPath, paired) : 0;
    }
    case "install-service":
    {
        // The old form still works — install-service <server> <token> — but the token now goes into
        // the config file instead of the service definition, which every user can read.
        var config = opts.Positional.Count >= 2 ? opts.ToConfig() : ClientConfigFile.Load(configPath);
        if (config is null)
        {
            Console.Error.WriteLine($"No settings at {configPath}. Pair first: RemoteCmd.Client pair <server> <code>");
            return 1;
        }
        if (opts.Positional.Count >= 2) ClientConfigFile.Save(configPath, config);
        return InstallService(opts.ServiceName, configPath, config);
    }
    case "uninstall-service":
        return ServiceInstaller.Uninstall(opts.ServiceName);
    default:
    {
        var config = opts.Positional.Count >= 2 ? opts.ToConfig() : ClientConfigFile.Load(configPath);
        if (config is null)
        {
            if (args.Length == 0) PrintUsage();
            else Console.Error.WriteLine($"No settings at {configPath} and no <server> <token> given.");
            return 1;
        }
        if (opts.KeepAwake && !config.KeepAwake) config = config with { KeepAwake = true };
        await RunHost(config, opts.Dashboard);
        return 0;
    }
}

static int InstallService(string serviceName, string configPath, ClientConfig config)
{
    if (!ServiceInstaller.IsPrivileged())
    {
        Console.Error.WriteLine("Installing a service needs an elevated prompt (Windows) or sudo.");
        return 13;
    }
    var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve executable path");
    return ServiceInstaller.Install(new ServiceSpec(
        serviceName, $"RemoteCmd Client ({config.Name})", exe, ["--service", "--config", configPath]));
}

static async Task RunHost(ClientConfig config, bool dashboard)
{
    var builder = Host.CreateApplicationBuilder();
    builder.Services.AddSingleton(config);
    builder.Services.AddHostedService<PollWorker>();

    // No-ops unless actually launched by the SCM / systemd, so console runs are unaffected.
    builder.Services.AddWindowsService(o => o.ServiceName = "RemoteCmdClient");
    builder.Services.AddSystemd();

    using var keepAwake = config.KeepAwake
        ? KeepAwake.Start(message => Console.Error.WriteLine(message))
        : null;

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

static void PrintUsage()
{
    Console.WriteLine($"RemoteCmd Client {ClientStats.Version}");
    Console.WriteLine();
    Console.WriteLine("Pair this machine with a relay (the code comes from the relay's dashboard):");
    Console.WriteLine("  RemoteCmd.Client pair <server> <code> [--name <alias>] [--install-service]");
    Console.WriteLine();
    Console.WriteLine("Run in the foreground with the saved settings, or with a token directly:");
    Console.WriteLine("  RemoteCmd.Client [--dashboard] [--keep-awake]");
    Console.WriteLine("  RemoteCmd.Client <server> <token> [--name <alias>] [--dashboard] [--keep-awake]");
    Console.WriteLine();
    Console.WriteLine("Run as a system service (Windows Service / systemd / launchd; needs admin or sudo):");
    Console.WriteLine("  RemoteCmd.Client install-service            uses the saved settings");
    Console.WriteLine("  RemoteCmd.Client uninstall-service");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  <server>             host, host:port or URL; a bare host means https on port 7890");
    Console.WriteLine("  --name <alias>       how the machine shows up on the relay (default: machine name)");
    Console.WriteLine("  --config <file>      settings file (default: " + ClientConfigFile.DefaultPath() + ")");
    Console.WriteLine($"  --service-name <n>   service name, for several clients on one machine (default: {ClientConfigFile.DefaultServiceName})");
    Console.WriteLine("  --dashboard          live status header + scrolling log");
    Console.WriteLine("  --keep-awake         keep the machine from sleeping while the client runs");
    Console.WriteLine("  --version            print version and exit");
}
