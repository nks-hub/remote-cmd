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
    /// Cely smysl --open-status je dashboard pouzitelny bez tokenu, vcetne toho, co se spustilo.
    /// Relay posloucha jen na localhostu, takze kdo dosahne na port, precte si tytez prikazy
    /// i v seznamu procesu.
    /// </summary>
    [Fact]
    public async Task HistoryShowsCommandTextToAnonymousViewers()
    {
        var id = Guid.NewGuid().ToString("N");
        await _http.GetAsync($"/api/poll?token={OpenStatusFactory.Token}&clientId={id}&name=open-secret");
        await _http.PostAsJsonAsync($"/api/exec?token={OpenStatusFactory.Token}&client=open-secret",
            new { command = "echo open-dashboard", timeoutSeconds = 1 });

        var anonymous = await _http.GetStringAsync("/api/events?limit=200");

        Assert.Contains("echo open-dashboard", anonymous);
        Assert.Contains("exec", anonymous);
    }

    /// <summary>
    /// Detail prikazu je to hlavni, kvuli cemu se na stranku kouka, takze v otevrenem rezimu
    /// token nechce. Spousteni prikazu a prenosy zamcene zustavaji (viz testy vyse).
    /// </summary>
    [Fact]
    public async Task CommandDetailIsOpenToo()
    {
        // Asserting "not 401" passed on the 404 this endpoint returns for an unknown id, so it would
        // have stayed green even if the endpoint had been removed. Run a real command and read its
        // stored output back with no token at all — that is the behaviour being claimed.
        var id = Guid.NewGuid().ToString("N");
        await _http.GetAsync($"/api/poll?token={OpenStatusFactory.Token}&clientId={id}&name=open-detail");
        await _http.PostAsJsonAsync($"/api/exec?token={OpenStatusFactory.Token}&client=open-detail",
            new { command = "echo detail-is-open", timeoutSeconds = 1 });

        var events = await _http.GetFromJsonAsync<EventsDto>("/api/events?limit=200");
        var exec = events!.Events.Last(e => e.Kind == "exec");

        var res = await _http.GetAsync($"/api/command?id={exec.Id}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("detail-is-open", await res.Content.ReadAsStringAsync());
    }

    private record EventsDto(List<EventDto> Events);
    private record EventDto(string Kind, string? Id);
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

    // The open dashboard is only open to this machine, so the test peer has to be one.
    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => TestPeer.At(builder.UseEnvironment("Testing"), System.Net.IPAddress.Loopback);

    protected override void Dispose(bool disposing)
    {
        Environment.SetEnvironmentVariable("REMOTECMD_OPEN_STATUS", null);
        base.Dispose(disposing);
    }
}
