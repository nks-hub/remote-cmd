using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Text.Json;
using RemoteCmd.Shared;
using Xunit;

namespace RemoteCmd.Tests;

/// <summary>The dashboard's files, and the management endpoints the redesigned pages lean on.</summary>
[Collection("CryptoSerial")]
public class DashboardWebTests
{
    [Fact]
    public void EveryPageAndScriptIsBundled()
    {
        foreach (var name in new[] { "index.html", "login.html", "app.css", "app.js", "login.js" })
            Assert.True(WebAssets.Exists(name), $"{name} is not embedded");
    }

    /// <summary>
    /// A stray apostrophe inside a JavaScript string once stopped the whole management page, pairing
    /// included, and nothing noticed. Node checks the syntax when it is installed (it is on CI).
    /// </summary>
    [Fact]
    public void EveryScriptParses()
    {
        var node = FindOnPath(OperatingSystem.IsWindows() ? "node.exe" : "node");
        if (node is null) return;
        foreach (var name in WebAssets.Names.Where(n => n.EndsWith(".js")))
        {
            var file = Path.Combine(Path.GetTempPath(), $"rcmd-{Guid.NewGuid():N}-{name}");
            File.WriteAllText(file, WebAssets.Text(name));
            try
            {
                using var p = Process.Start(new ProcessStartInfo(node, $"--check \"{file}\"") { RedirectStandardError = true, UseShellExecute = false })!;
                var err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                Assert.True(p.ExitCode == 0, $"{name}: {err}");
            }
            finally { File.Delete(file); }
        }
    }

    /// <summary>Every id the script looks up exists in the page, so no handler silently binds to nothing.</summary>
    [Fact]
    public void EveryElementTheScriptUsesIsOnThePage()
    {
        var html = WebAssets.Text("index.html");
        var ids = System.Text.RegularExpressions.Regex.Matches(WebAssets.Text("app.js"), @"\$\('([A-Za-z][\w-]*)'\)")
            .Select(m => m.Groups[1].Value).Distinct();
        foreach (var id in ids) Assert.Contains($"id=\"{id}\"", html);
    }

    [Fact]
    public async Task PagesComeWithAStrictContentSecurityPolicy()
    {
        using var relay = new DashboardRelay();
        foreach (var path in new[] { "/", "/manage", "/login", "/assets/app.js", "/assets/app.css" })
        {
            var res = await relay.DashboardResponse("GET", path);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Contains("script-src 'self'", res.Headers.GetValues("Content-Security-Policy").Single());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await relay.DashboardResponse("GET", "/assets/index.html")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await relay.DashboardResponse("GET", "/assets/nope.js")).StatusCode);
    }

    [Fact]
    public async Task TheFilesDoNotExistOnTheRelayPort()
    {
        using var relay = new DashboardRelay();
        var http = relay.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/assets/app.js")).StatusCode);
    }

    /// <summary>The sign-in page has to load its own script before anyone has signed in.</summary>
    [Fact]
    public async Task TheSignInPageWorksBeforeSigningIn()
    {
        using var relay = new DashboardRelay();
        Assert.Equal(200, (await relay.Dashboard("POST", "/api/admin/password", new { password = "correct horse" })).Status);
        Assert.Equal(200, (await relay.Dashboard("GET", "/assets/login.js")).Status);
        Assert.Equal(200, (await relay.Dashboard("GET", "/login")).Status);
        Assert.Equal(401, (await relay.Dashboard("GET", "/api/admin/info")).Status);
    }

    /// <summary>
    /// A redeemed code stays visible with what became of it: the dashboard can then tell "paired and
    /// online" from "paired, but its client never connected" — before, both looked like nothing.
    /// </summary>
    [Fact]
    public async Task ARedeemedCodeReportsWhetherTheMachineArrived()
    {
        using var relay = new DashboardRelay();
        var code = await relay.NewPairingCode("lab-pc");
        var http = relay.CreateClient();
        var (request, state) = Pairing.Begin(code, "host");
        var grant = Pairing.Finish(code, state, (await (await http.PostAsJsonAsync("/api/pair", request)).Content.ReadFromJsonAsync<PairResponse>())!);

        var recent = await Recent(relay);
        Assert.Equal("lab-pc", recent.GetProperty("name").GetString());
        Assert.False(recent.GetProperty("polled").GetBoolean());

        http.DefaultRequestHeaders.Add("X-Token", grant.Token);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/poll")).StatusCode);
        recent = await Recent(relay);
        Assert.True(recent.GetProperty("connected").GetBoolean());
        Assert.False(string.IsNullOrEmpty(recent.GetProperty("sessionId").GetString()));
    }

    [Fact]
    public async Task ACodeCanCarryItsOwnRelayAddress()
    {
        using var relay = new DashboardRelay();
        var (status, body, _) = await relay.Dashboard("POST", "/api/admin/pair", new { name = "x", serverUrl = "http://192.0.2.10:7890" });
        Assert.Equal(200, status);
        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal("http://192.0.2.10:7890", root.GetProperty("serverUrl").GetString());
        Assert.All(root.GetProperty("install").EnumerateArray(), c => Assert.Contains("http://192.0.2.10:7890", c.GetProperty("command").GetString()));
        // The CLI still reads the labelled form.
        Assert.Contains("Windows (PowerShell as admin)", root.GetProperty("commands").EnumerateObject().Select(p => p.Name));

        Assert.Equal(400, (await relay.Dashboard("POST", "/api/admin/pair", new { serverUrl = "ftp://192.0.2.10" })).Status);
        // Only scheme, host and port reach a command the operator runs as administrator.
        var (_, sneaky, _) = await relay.Dashboard("POST", "/api/admin/pair", new { serverUrl = "http://192.0.2.10:7890/x';calc;'" });
        Assert.Equal("http://192.0.2.10:7890", JsonDocument.Parse(sneaky).RootElement.GetProperty("serverUrl").GetString());
        Assert.Equal(400, (await relay.Dashboard("POST", "/api/admin/settings", new { publicUrl = "not a url" })).Status);
    }

    /// <summary>An old client still polling with a key nobody issued shows up by name, ready to pair again.</summary>
    [Fact]
    public async Task AMachineWithAnUnknownKeyIsNamed()
    {
        using var relay = new DashboardRelay();
        var http = relay.CreateClient();
        http.DefaultRequestHeaders.Add("X-Token", "an-old-key-nobody-knows-any-more");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/poll?clientId=abc&name=OLD-PC%07&host=old-pc")).StatusCode);

        var (_, body, _) = await relay.Dashboard("GET", "/api/admin/unknown-keys");
        var machine = JsonDocument.Parse(body).RootElement.GetProperty("machines").EnumerateArray().Single();
        Assert.Equal("OLD-PC", machine.GetProperty("name").GetString()); // control characters dropped
        Assert.Equal("old-pc", machine.GetProperty("host").GetString());
        Assert.Equal(1, machine.GetProperty("attempts").GetInt32());
    }

    [Fact]
    public async Task AnOnlineMachineCannotBeRemovedFromTheList()
    {
        using var relay = new DashboardRelay();
        var token = await relay.IssueToken("client", "box");
        var http = relay.CreateClient();
        http.DefaultRequestHeaders.Add("X-Token", token);
        await http.GetAsync("/api/poll");

        var (_, list, _) = await relay.Dashboard("GET", "/api/clients");
        var id = JsonDocument.Parse(list).RootElement.GetProperty("clients").EnumerateArray().Single().GetProperty("id").GetString();
        Assert.Equal(409, (await relay.Dashboard("DELETE", $"/api/admin/clients/{id}")).Status);
        Assert.Equal(404, (await relay.Dashboard("DELETE", "/api/admin/clients/nobody")).Status);
    }

    [Fact]
    public async Task TheDashboardListsThisMachinesAddresses()
    {
        using var relay = new DashboardRelay();
        var (status, body, _) = await relay.Dashboard("GET", "/api/admin/addresses");
        Assert.Equal(200, status);
        var root = JsonDocument.Parse(body).RootElement;
        Assert.StartsWith("http", root.GetProperty("current").GetString());
        Assert.All(root.GetProperty("addresses").EnumerateArray(), a => Assert.StartsWith("http://", a.GetProperty("url").GetString()));
    }

    [Theory]
    [InlineData(NetworkInterfaceType.Ethernet, "Ethernet", "Intel(R) Ethernet Connection", "192.168.88.254", RelayAddresses.Lan)]
    [InlineData(NetworkInterfaceType.Unknown, "chatujme_wg", "WireGuard Tunnel", "10.254.0.7", RelayAddresses.Vpn)]
    [InlineData(NetworkInterfaceType.Ppp, "NKS-L2TP", "WAN Miniport (L2TP)", "10.66.6.200", RelayAddresses.Vpn)]
    [InlineData(NetworkInterfaceType.Ethernet, "vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", "172.19.48.1", RelayAddresses.Virtual)]
    [InlineData(NetworkInterfaceType.Ethernet, "docker0", "docker0", "172.17.0.1", RelayAddresses.Virtual)]
    [InlineData(NetworkInterfaceType.Ethernet, "eth0", "eth0", "203.0.113.5", RelayAddresses.Public)]
    [InlineData(NetworkInterfaceType.Wireless80211, "Wi-Fi", "Intel(R) Wi-Fi 6E", "10.0.0.20", RelayAddresses.Lan)]
    public void AddressesAreSortedOutByWhatTheyLeadTo(NetworkInterfaceType type, string name, string description, string ip, string kind)
        => Assert.Equal(kind, RelayAddresses.Classify(type, name, description, IPAddress.Parse(ip)));

    private static async Task<JsonElement> Recent(DashboardRelay relay)
    {
        var (_, body, _) = await relay.Dashboard("GET", "/api/admin/pair");
        return JsonDocument.Parse(body).RootElement.GetProperty("recent").EnumerateArray().Single();
    }

    private static string? FindOnPath(string exe)
        => (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, exe))
            .FirstOrDefault(File.Exists);
}
