using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace RemoteCmd.Tests;

/// <summary>
/// With --open-status the dashboard opens straight away: status, client list and history need no
/// token. Anything that reaches a machine — exec, transfers, the client protocol — still does.
/// </summary>
[Collection("CryptoSerial")]
public class OpenStatusTests : IClassFixture<OpenStatusFactory>
{
    private readonly HttpClient _http;

    public OpenStatusTests(OpenStatusFactory factory) => _http = factory.CreateClient();

    [Theory]
    [InlineData("/api/status")]
    [InlineData("/api/clients")]
    [InlineData("/api/events")]
    public async Task ReadOnlyEndpointsNeedNoToken(string path)
    {
        var res = await _http.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Theory]
    [InlineData("/api/poll")]
    [InlineData("/api/file-poll")]
    [InlineData("/api/file-data")]
    [InlineData("/api/download?path=/etc/hosts")]
    // Command output can carry passwords and keys, so it is never part of the open overview.
    [InlineData("/api/command?id=whatever")]
    public async Task EverythingElseStillNeedsAToken(string path)
    {
        var res = await _http.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task ExecStillNeedsAToken()
    {
        var res = await _http.PostAsJsonAsync("/api/exec", new { command = "whoami" });

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task UploadStillNeedsAToken()
    {
        var res = await _http.PostAsync("/api/upload?path=/tmp/x", new ByteArrayContent([1, 2, 3]));

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    /// <summary>
    /// A command line carries credentials as readily as its output does, so the open history shows
    /// that something ran without saying what — the token holder still sees the text.
    /// </summary>
    [Fact]
    public async Task HistoryHidesCommandTextFromAnonymousViewers()
    {
        var id = Guid.NewGuid().ToString("N");
        await _http.GetAsync($"/api/poll?token={OpenStatusFactory.Token}&clientId={id}&name=open-secret");
        await _http.PostAsJsonAsync($"/api/exec?token={OpenStatusFactory.Token}&client=open-secret",
            new { command = "mysql -phunter2", timeoutSeconds = 1 });

        var anonymous = await _http.GetStringAsync("/api/events?limit=200");
        Assert.DoesNotContain("hunter2", anonymous);
        Assert.Contains("exec", anonymous);            // the activity itself is still visible

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/events?limit=200");
        req.Headers.Add("X-Token", OpenStatusFactory.Token);
        var withToken = await (await _http.SendAsync(req)).Content.ReadAsStringAsync();
        Assert.Contains("hunter2", withToken);
    }

    /// <summary>
    /// Offering a wrong token is a failed attempt even on the endpoints that need none, otherwise
    /// the redaction itself would tell an attacker when a guess was right, throttle-free.
    /// </summary>
    [Fact]
    public async Task AWrongTokenIsStillRefusedOnTheOpenEndpoints()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        req.Headers.Add("X-Token", "definitely-not-the-token");

        var res = await _http.SendAsync(req);

        Assert.True(res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests);
    }
}

public class OpenStatusFactory : WebApplicationFactory<Program>
{
    public const string Token = "open-status-token-1234567890";

    public OpenStatusFactory()
    {
        Environment.SetEnvironmentVariable("REMOTECMD_TOKEN", Token);
        Environment.SetEnvironmentVariable("REMOTECMD_NO_TLS", "1");
        Environment.SetEnvironmentVariable("REMOTECMD_OPEN_STATUS", "1");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");

    protected override void Dispose(bool disposing)
    {
        Environment.SetEnvironmentVariable("REMOTECMD_OPEN_STATUS", null);
        base.Dispose(disposing);
    }
}
