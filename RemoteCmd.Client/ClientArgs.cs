namespace RemoteCmd.Client;

/// <summary>The client's command line: an optional verb, positional values and switches in any order.</summary>
public sealed class ClientArgs
{
    public string? Verb { get; private set; }
    public List<string> Positional { get; } = [];
    public string? Name { get; private set; }
    public string? ConfigPath { get; private set; }
    public string ServiceName { get; private set; } = ClientConfigFile.DefaultServiceName;
    public bool Dashboard { get; private set; }
    public bool KeepAwake { get; private set; }
    public bool InstallService { get; private set; }

    private static readonly HashSet<string> Verbs = ["pair", "install-service", "uninstall-service"];

    public static ClientArgs Parse(string[] args)
    {
        var o = new ClientArgs();
        var start = 0;
        if (args.Length > 0 && Verbs.Contains(args[0])) { o.Verb = args[0]; start = 1; }

        for (var i = start; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--service": break; // marks a run started by the service manager; nothing to read
                case "--dashboard": o.Dashboard = true; break;
                case "--keep-awake": o.KeepAwake = true; break;
                case "--install-service": o.InstallService = true; break;
                case "--name" when i + 1 < args.Length: o.Name = args[++i]; break;
                case "--config" when i + 1 < args.Length: o.ConfigPath = args[++i]; break;
                case "--service-name" when i + 1 < args.Length: o.ServiceName = args[++i]; break;
                default: o.Positional.Add(args[i]); break;
            }
        }
        return o;
    }

    /// <summary>Settings from <c>&lt;server&gt; &lt;token&gt;</c> given directly, the way clients ran before pairing.</summary>
    public ClientConfig ToConfig()
        => new(Positional[0], Positional[1], Name ?? Environment.MachineName, KeepAwake);
}
