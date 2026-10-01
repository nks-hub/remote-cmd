using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RemoteCmd.Shared;

/// <summary>The server's command line: an optional verb, its values, tokens and switches in any order.</summary>
public sealed class ServerArgs
{
    public string? Verb { get; private set; }
    public string? SubVerb { get; private set; }
    public List<string> Tokens { get; } = [];
    public List<string> Values { get; } = [];
    public string? ConfigPath { get; private set; }
    public int? Port { get; private set; }
    public int? AdminPort { get; private set; }
    public bool NoTls { get; private set; }
    public bool Dashboard { get; private set; }
    public string? Name { get; private set; }
    public string? Role { get; private set; }
    public string? Password { get; private set; }
    public int? TtlMinutes { get; private set; }
    public bool Clear { get; private set; }
    public string ServiceName { get; private set; } = "RemoteCmdServer";

    private static readonly HashSet<string> Verbs = ["install-service", "uninstall-service", "pair", "token", "set-password"];

    public static ServerArgs Parse(string[] args)
    {
        var o = new ServerArgs();
        var start = 0;
        if (args.Length > 0 && Verbs.Contains(args[0])) { o.Verb = args[0]; start = 1; }
        if (o.Verb == "token" && args.Length > 1 && !args[1].StartsWith('-')) { o.SubVerb = args[1]; start = 2; }

        for (var i = start; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--service": break; // marks a run started by the service manager
                case "--no-tls": o.NoTls = true; break;
                case "--dashboard": o.Dashboard = true; break;
                case "--clear": o.Clear = true; break;
                // Superseded by the loopback dashboard; accepted so old start scripts keep working.
                case "--open-status": break;
                case "--config": o.ConfigPath = Next(); break;
                case "--port": o.Port = int.Parse(Next()); break;
                case "--admin-port": o.AdminPort = int.Parse(Next()); break;
                case "--name": o.Name = Next(); break;
                case "--role": o.Role = Next(); break;
                case "--password": o.Password = Next(); break;
                case "--ttl": o.TtlMinutes = int.Parse(Next()); break;
                case "--service-name": o.ServiceName = Next(); break;
                default:
                    // Unknown switches belong to ASP.NET's own configuration (--urls=…, --environment=…).
                    if (args[i].StartsWith('-')) break;
                    if (o.Verb == null) o.Tokens.Add(args[i]); else o.Values.Add(args[i]);
                    break;
            }
        }
        return o;
    }

    public int ResolvedAdminPort()
    {
        var port = Port ?? (int.TryParse(Environment.GetEnvironmentVariable("REMOTECMD_PORT"), out var p) ? p : 7890);
        return AdminPort ?? (int.TryParse(Environment.GetEnvironmentVariable("REMOTECMD_ADMIN_PORT"), out var a) ? a : port + 1);
    }
}

/// <summary>
/// Management verbs. Everything that touches a running relay goes through its dashboard API on
/// loopback — pairing codes live in its memory, and it owns the settings file while it runs.
/// </summary>
public static class ServerCli
{
    public static async Task<int> RunAsync(ServerArgs o)
    {
        try
        {
            return o.Verb switch
            {
                "install-service" => InstallService(o),
                "uninstall-service" => ServiceInstaller.Uninstall(o.ServiceName),
                "pair" => await PairAsync(o),
                "token" => await TokenAsync(o),
                "set-password" => await SetPasswordAsync(o),
                _ => 1,
            };
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Cannot reach the relay's dashboard on port {o.ResolvedAdminPort()} ({ex.Message}). Is the relay running?");
            return 1;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int InstallService(ServerArgs o)
    {
        if (!ServiceInstaller.IsPrivileged())
        {
            Console.Error.WriteLine("Installing a service needs an elevated prompt (Windows) or sudo.");
            return 13;
        }
        if (o.Values.Count > 0)
        {
            Console.Error.WriteLine("Tokens do not go into a service definition, which other users can read.");
            Console.Error.WriteLine("Store them first: RemoteCmd.Server token import <token> --name <label>");
            return 1;
        }

        var config = Path.GetFullPath(o.ConfigPath ?? ServerStore.DefaultPath());
        ServerStore.Open(config); // created now, so its permissions are set before the service runs
        List<string> args = ["--service", "--config", config];
        if (o.Port is { } port) args.AddRange(["--port", port.ToString()]);
        if (o.AdminPort is { } admin) args.AddRange(["--admin-port", admin.ToString()]);
        if (o.NoTls) args.Add("--no-tls");

        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve executable path");
        var result = ServiceInstaller.Install(new ServiceSpec(o.ServiceName, "RemoteCmd Relay Server", exe, args));
        if (result == 0) Console.WriteLine($"Dashboard: http://localhost:{o.ResolvedAdminPort()}  Settings: {config}");
        return result;
    }

    private static async Task<int> PairAsync(ServerArgs o)
    {
        using var api = await AdminApi.ConnectAsync(o);
        var res = await api.Http.PostAsJsonAsync("/api/admin/pair", new { name = o.Name, ttlMinutes = o.TtlMinutes });
        res.EnsureSuccessStatusCode();
        var pair = await res.Content.ReadFromJsonAsync<JsonElement>();
        Console.WriteLine($"Pairing code: {pair.GetProperty("code").GetString()}   (valid until {pair.GetProperty("expiresUtc").GetDateTime().ToLocalTime():HH:mm})");
        Console.WriteLine();
        foreach (var cmd in pair.GetProperty("commands").EnumerateObject())
        {
            Console.WriteLine($"{cmd.Name}:");
            Console.WriteLine($"  {cmd.Value.GetString()}");
        }
        return 0;
    }

    private static async Task<int> TokenAsync(ServerArgs o)
    {
        using var api = await AdminApi.ConnectAsync(o);
        switch (o.SubVerb)
        {
            case "add":
            {
                var res = await api.Http.PostAsJsonAsync("/api/admin/credentials",
                    new { name = o.Name, role = o.Role ?? "controller" });
                if (!res.IsSuccessStatusCode) return await Fail(res);
                var body = await res.Content.ReadFromJsonAsync<JsonElement>();
                Console.WriteLine($"Token for '{body.GetProperty("name").GetString()}' ({body.GetProperty("role").GetString()}). It is shown only now:");
                Console.WriteLine(body.GetProperty("token").GetString());
                return 0;
            }
            case "import":
            {
                if (o.Values.Count != 1) throw new ArgumentException("Usage: RemoteCmd.Server token import <token> --name <label> [--role legacy|client|controller]");
                var res = await api.Http.PostAsJsonAsync("/api/admin/credentials/import",
                    new { token = o.Values[0], name = o.Name, role = o.Role ?? "legacy" });
                if (!res.IsSuccessStatusCode) return await Fail(res);
                Console.WriteLine("Imported. The relay now accepts it without it being on the command line.");
                return 0;
            }
            case "list":
            {
                var list = await api.Http.GetFromJsonAsync<JsonElement>("/api/admin/credentials");
                foreach (var c in list.GetProperty("credentials").EnumerateArray())
                    Console.WriteLine($"{c.GetProperty("id").GetString(),-14} {c.GetProperty("role").GetString(),-10} {c.GetProperty("name").GetString()}");
                return 0;
            }
            case "revoke":
            {
                if (o.Values.Count != 1) throw new ArgumentException("Usage: RemoteCmd.Server token revoke <id>");
                var res = await api.Http.DeleteAsync($"/api/admin/credentials/{Uri.EscapeDataString(o.Values[0])}");
                if (!res.IsSuccessStatusCode) return await Fail(res);
                Console.WriteLine("Revoked.");
                return 0;
            }
            default:
                throw new ArgumentException("Usage: RemoteCmd.Server token add|import|list|revoke");
        }
    }

    private static async Task<int> SetPasswordAsync(ServerArgs o)
    {
        using var api = await AdminApi.ConnectAsync(o);
        string? password = null;
        if (!o.Clear)
        {
            password = ReadSecret("New dashboard password: ");
            if (password != ReadSecret("Repeat: ")) throw new ArgumentException("The passwords differ.");
            if (password.Length < 8) throw new ArgumentException("Use at least 8 characters.");
        }
        var res = await api.Http.PostAsJsonAsync("/api/admin/password", new { password });
        if (!res.IsSuccessStatusCode) return await Fail(res);
        Console.WriteLine(o.Clear ? "Password removed; the dashboard is open to this machine." : "Password set.");
        return 0;
    }

    private static async Task<int> Fail(HttpResponseMessage res)
    {
        Console.Error.WriteLine($"Refused ({(int)res.StatusCode}): {await res.Content.ReadAsStringAsync()}");
        return 1;
    }

    internal static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); continue; }
            chars.Add(key.KeyChar);
        }
        Console.WriteLine();
        return new string([.. chars]);
    }

    /// <summary>An HTTP client for the dashboard API, signed in when the dashboard has a password.</summary>
    private sealed class AdminApi : IDisposable
    {
        public HttpClient Http { get; }

        private AdminApi(HttpClient http) => Http = http;

        public static async Task<AdminApi> ConnectAsync(ServerArgs o)
        {
            var http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
            {
                BaseAddress = new Uri($"http://localhost:{o.ResolvedAdminPort()}"),
                Timeout = TimeSpan.FromSeconds(15),
            };
            http.DefaultRequestHeaders.Add(AdminGate.CsrfHeader, "1");

            var session = await http.GetFromJsonAsync<JsonElement>("/api/admin/session");
            if (session.GetProperty("passwordRequired").GetBoolean() && !session.GetProperty("signedIn").GetBoolean())
            {
                var password = o.Password ?? ReadSecret("Dashboard password: ");
                var login = await http.PostAsJsonAsync("/api/admin/login", new { password });
                if (!login.IsSuccessStatusCode) throw new ArgumentException("Wrong dashboard password.");
            }
            return new AdminApi(http);
        }

        public void Dispose() => Http.Dispose();
    }
}
