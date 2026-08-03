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
