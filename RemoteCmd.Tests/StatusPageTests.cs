using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace RemoteCmd.Tests;

/// <summary>
/// The status page must be usable without ever putting the token in a URL: the page itself loads
/// unauthenticated (it carries no data) and the API behind it accepts the X-Token header.
/// </summary>
[Collection("CryptoSerial")]
public class StatusPageTests : IClassFixture<RemoteCmdFactory>
{
    private readonly HttpClient _http;

    public StatusPageTests(RemoteCmdFactory factory) => _http = factory.CreateClient();

    [Fact]
    public async Task PageLoadsWithoutAToken()
    {
        var res = await _http.GetAsync("/ui");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("relay token", html);          // asks for it in the browser
        Assert.Contains("sessionStorage", html);        // keeps it out of the URL
        Assert.DoesNotContain(RemoteCmdFactory.Token, html);
    }

    [Fact]
    public async Task ApiAcceptsTheTokenAsAHeader()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/clients");
        req.Headers.Add("X-Token", RemoteCmdFactory.Token);

        var res = await _http.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task ApiWithoutATokenIsRefused()
    {
        var res = await _http.GetAsync("/api/clients");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task RootPageDoesNotAdvertiseWhoIsConnected()
    {
        var body = await _http.GetStringAsync("/");

        Assert.Contains("Remote CMD Relay Server", body);
        Assert.DoesNotContain("Connected clients", body);
    }
}
