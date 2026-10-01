using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using RemoteCmd.Shared;
using Xunit;

namespace RemoteCmd.Tests;

/// <summary>
/// A relay with no command-line tokens at all: everything goes through the dashboard, pairing and
/// stored credentials. Each test gets its own relay, because they change who is allowed in.
/// </summary>
public sealed class DashboardRelay : WebApplicationFactory<Program>
{
    /// <summary>The relay port defaults to 7890, so the dashboard listens on the next one.</summary>
    public const int AdminPort = 7891;

    public DashboardRelay()
    {
        Environment.SetEnvironmentVariable("REMOTECMD_TOKEN", null);
        Environment.SetEnvironmentVariable("REMOTECMD_TOKENS", null);
        Environment.SetEnvironmentVariable("REMOTECMD_NO_TLS", "1");
        TestConfig.Fresh();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => TestPeer.At(builder.UseEnvironment("Testing"), IPAddress.Loopback);

    /// <summary>
    /// A request as it arrives on the loopback dashboard listener. The test host has no sockets,
    /// so the listener is chosen by setting the local port the way Kestrel would.
    /// </summary>
    public async Task<(int Status, string Body, string? SetCookie)> Dashboard(
        string method, string path, object? body = null, string host = "localhost:7891",
        bool csrf = true, string? cookie = null)
    {
        var handler = Server.CreateHandler(c =>
        {
            c.Connection.LocalPort = AdminPort;
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:7891") };
        var req = new HttpRequestMessage(new HttpMethod(method), path);
        req.Headers.Host = host;
        if (csrf) req.Headers.Add("X-RemoteCmd", "1");
        if (cookie != null) req.Headers.Add("Cookie", cookie);
        if (body != null) req.Content = JsonContent.Create(body);
        var res = await http.SendAsync(req);
        var setCookie = res.Headers.TryGetValues("Set-Cookie", out var values) ? values.FirstOrDefault() : null;
        return ((int)res.StatusCode, await res.Content.ReadAsStringAsync(), setCookie);
    }

    public async Task<string> IssueToken(string role, string name)
    {
        var (status, body, _) = await Dashboard("POST", "/api/admin/credentials", new { name, role });
        Assert.True(status == 200, $"{status}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()!;
    }

    public async Task<string> NewPairingCode(string? name = null)
    {
        var (status, body, _) = await Dashboard("POST", "/api/admin/pair", new { name });
        Assert.Equal(200, status);
        return JsonDocument.Parse(body).RootElement.GetProperty("code").GetString()!;
    }
}

[Collection("CryptoSerial")]
public class DashboardAccessTests
{
    [Fact]
    public async Task TheDashboardOpensWithoutATokenOnLoopback()
    {
        using var relay = new DashboardRelay();
        var (status, body, _) = await relay.Dashboard("GET", "/");
        Assert.Equal(200, status);
        Assert.Contains("<!doctype html>", body);

        Assert.Equal(200, (await relay.Dashboard("GET", "/api/admin/info")).Status);
        Assert.Equal(200, (await relay.Dashboard("GET", "/api/clients")).Status);
    }

    [Fact]
    public async Task ManagementIsNotReachableOnTheRelayPort()
    {
        using var relay = new DashboardRelay();
        var http = relay.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/admin/info")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync("/api/admin/pair", new { })).StatusCode);
        // Pages are sent on to the dashboard listener rather than served here.
        Assert.NotEqual(HttpStatusCode.OK, (await http.GetAsync("/login")).StatusCode);
    }

    /// <summary>DNS rebinding: a page from elsewhere whose host name now resolves to 127.0.0.1.</summary>
    [Fact]
    public async Task AForeignHostNameIsRefused()
    {
        using var relay = new DashboardRelay();
        Assert.Equal(400, (await relay.Dashboard("GET", "/api/admin/info", host: "evil.example:7891")).Status);
        Assert.Equal(200, (await relay.Dashboard("GET", "/api/admin/info", host: "127.0.0.1:7891")).Status);
    }

    /// <summary>A cross-site form or fetch cannot add a custom header, so changes require one.</summary>
    [Fact]
    public async Task ChangesWithoutTheCsrfHeaderAreRefused()
    {
        using var relay = new DashboardRelay();
        Assert.Equal(403, (await relay.Dashboard("POST", "/api/admin/pair", new { }, csrf: false)).Status);
        var ok = await relay.Dashboard("POST", "/api/admin/pair", new { });
        Assert.True(ok.Status == 200, ok.Body);
    }

    [Fact]
    public async Task APasswordLocksTheDashboardUntilSignedIn()
    {
        using var relay = new DashboardRelay();
        var (setStatus, _, ownCookie) = await relay.Dashboard("POST", "/api/admin/password", new { password = "correct horse" });
        Assert.Equal(200, setStatus);
        Assert.NotNull(ownCookie); // whoever set it stays signed in

        Assert.Equal(401, (await relay.Dashboard("GET", "/api/admin/info")).Status);
        Assert.Equal(401, (await relay.Dashboard("POST", "/api/admin/login", new { password = "wrong one!" })).Status);

        var (loginStatus, _, cookie) = await relay.Dashboard("POST", "/api/admin/login", new { password = "correct horse" });
        Assert.Equal(200, loginStatus);
        var session = cookie!.Split(';')[0];
        Assert.Equal(200, (await relay.Dashboard("GET", "/api/admin/info", cookie: session)).Status);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AClientTokenCannotSendCommands()
    {
        using var relay = new DashboardRelay();
        var token = await relay.IssueToken("client", "worker-1");
        var http = relay.CreateClient();
        http.DefaultRequestHeaders.Add("X-Token", token);

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/poll?clientId=x&name=x")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await http.PostAsJsonAsync("/api/exec?client=worker-1", new { command = "whoami" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/clients")).StatusCode);
    }

    [Fact]
    public async Task AControllerTokenCannotPoseAsAClient()
    {
        using var relay = new DashboardRelay();
        var token = await relay.IssueToken("controller", "ci");
        var http = relay.CreateClient();
        http.DefaultRequestHeaders.Add("X-Token", token);

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/clients")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/poll?clientId=x&name=x")).StatusCode);
    }

    /// <summary>
    /// With a shared token, anyone holding it could poll under another machine's id and be handed
    /// its commands. A paired machine's identity comes from its credential, not from what it claims.
    /// </summary>
    [Fact]
    public async Task AClientIsWhoItsCredentialSays()
    {
        using var relay = new DashboardRelay();
        var token = await relay.IssueToken("client", "real-name");
        var http = relay.CreateClient();
        http.DefaultRequestHeaders.Add("X-Token", token);
        await http.GetAsync("/api/poll?clientId=someone-else&name=impostor");

        var (_, body, _) = await relay.Dashboard("GET", "/api/clients");
        var names = JsonDocument.Parse(body).RootElement.GetProperty("clients").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Equal(["real-name"], names);
    }

    [Fact]
    public async Task PairingTradesTheCodeForAWorkingClientToken()
    {
        using var relay = new DashboardRelay();
        var code = await relay.NewPairingCode("lab-pc");
        var http = relay.CreateClient();

        var (request, state) = Pairing.Begin(code, "whatever-hostname");
        var res = await http.PostAsJsonAsync("/api/pair", request);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var grant = Pairing.Finish(code, state, (await res.Content.ReadFromJsonAsync<PairResponse>())!);

        Assert.Equal("lab-pc", grant.Name); // the name given on the dashboard wins
        http.DefaultRequestHeaders.Add("X-Token", grant.Token);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/poll")).StatusCode);

        // Single use.
        var (again, _) = Pairing.Begin(code, "second");
        Assert.Equal(HttpStatusCode.Forbidden, (await relay.CreateClient().PostAsJsonAsync("/api/pair", again)).StatusCode);
    }

    [Fact]
    public async Task AWrongCodeGetsNothingAndBurnsTheCodeAfterAFewTries()
    {
        using var relay = new DashboardRelay();
        var code = await relay.NewPairingCode();
        var http = relay.CreateClient();

        for (var i = 0; i < PairingCodes.MaxFailures; i++)
        {
            // Right id, wrong proof: what someone guessing the rest of the code would send.
            var (forged, _) = Pairing.Begin("WRONG-CODE0", "x");
            forged.CodeId = Pairing.CodeId(code);
            var res = await http.PostAsJsonAsync("/api/pair", forged);
            Assert.True(res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests);
        }

        var (_, body, _) = await relay.Dashboard("GET", "/api/admin/pair");
        Assert.Empty(JsonDocument.Parse(body).RootElement.GetProperty("pending").EnumerateArray());
    }

    [Fact]
    public async Task RevokingCutsTheCredentialOffAtOnce()
    {
        using var relay = new DashboardRelay();
        var token = await relay.IssueToken("controller", "temp");
        var http = relay.CreateClient();
        http.DefaultRequestHeaders.Add("X-Token", token);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/clients")).StatusCode);

        var (_, list, _) = await relay.Dashboard("GET", "/api/admin/credentials");
        var id = JsonDocument.Parse(list).RootElement.GetProperty("credentials").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "temp").GetProperty("id").GetString();
        Assert.Equal(200, (await relay.Dashboard("DELETE", $"/api/admin/credentials/{id}")).Status);

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/clients")).StatusCode);
    }
}

public class ServerStoreTests
{
    [Fact]
    public void CredentialsSurviveARestartWithoutTheTokenBeingStored()
    {
        var path = Path.Combine(Path.GetTempPath(), "remotecmd-tests", Guid.NewGuid().ToString("N"), "server.json");
        var (_, token) = ServerStore.Open(path).Issue("box", CredentialRole.Client);

        Assert.DoesNotContain(token, File.ReadAllText(path));
        var reopened = ServerStore.Open(path);
        Assert.Equal(CredentialRole.Client, reopened.Authenticate(token)?.Role);
        Assert.Null(reopened.Authenticate(token + "x"));
    }

    [Fact]
    public void TheStoredKeyDecryptsWhatTheClientEncrypts()
    {
        var path = Path.Combine(Path.GetTempPath(), "remotecmd-tests", Guid.NewGuid().ToString("N"), "server.json");
        var (credential, token) = ServerStore.Open(path).Issue("box", CredentialRole.Client);
        var sealedText = new CryptoKey(token).EncryptString("hello");
        Assert.Equal("hello", credential.Crypto.DecryptString(sealedText));
    }

    [Fact]
    public void PasswordsAreCheckedAgainstTheirHash()
    {
        var path = Path.Combine(Path.GetTempPath(), "remotecmd-tests", Guid.NewGuid().ToString("N"), "server.json");
        var store = ServerStore.Open(path);
        store.SetPassword("hunter22hunter");
        Assert.DoesNotContain("hunter22hunter", File.ReadAllText(path));
        Assert.True(store.CheckPassword("hunter22hunter"));
        Assert.False(store.CheckPassword("hunter22hunteR"));
    }
}

public class PairingProtocolTests
{
    [Fact]
    public void BothSidesNeedTheSameCode()
    {
        var (request, state) = Pairing.Begin("ABCDE-FGHJK", "pc");
        var grant = new PairGrant { Token = "t", CredentialId = "c", Name = "pc" };

        Assert.Null(Pairing.Answer("ABCDE-FGHJM", request, () => grant)); // relay with another code
        var answer = Pairing.Answer("abcde fghjk", request, () => grant);  // typed sloppily, same code
        Assert.NotNull(answer);
        Assert.Equal("t", Pairing.Finish("ABCDE-FGHJK", state, answer!).Token);
    }

    [Fact]
    public void AClientWithTheWrongCodeCannotOpenTheGrant()
    {
        var (request, state) = Pairing.Begin("ABCDE-FGHJK", "pc");
        var answer = Pairing.Answer("ABCDE-FGHJK", request, () => new PairGrant { Token = "secret" })!;
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => Pairing.Finish("ZZZZZ-ZZZZZ", state, answer));
    }

    [Fact]
    public void NothingIsIssuedForAWrongProof()
    {
        var (request, _) = Pairing.Begin("ABCDE-FGHJK", "pc");
        var issued = false;
        Pairing.Answer("QQQQQ-QQQQQ", request, () => { issued = true; return new PairGrant(); });
        Assert.False(issued);
    }

    [Fact]
    public void CodesAreReadableAndNormalizeTheCrockfordWay()
    {
        var code = Pairing.NewCode();
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{5}-[0-9A-HJKMNP-TV-Z]{5}$", code);
        Assert.Equal("0123456789", Pairing.Normalize("O1234-56789"));
        Assert.Equal(Pairing.CodeId("ABCDE-FGHJK"), Pairing.CodeId("abcdefghjk"));
    }
}
