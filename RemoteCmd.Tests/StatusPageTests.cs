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

    /// <summary>
    /// Every value the page shows comes from a machine someone else controls — a command line, a
    /// file path, a client name. The page is only safe because it never assembles markup from that
    /// data, so the absence of the assignment is the invariant worth guarding.
    /// </summary>
    [Fact]
    public void ThePageNeverBuildsMarkupFromRelayData()
    {
        // The writes are what is banned, not the words: the page's own comments explain the rule
        // and name the properties while doing so.
        Assert.DoesNotMatch(@"(inner|outer)HTML\s*=", StatusPage.Html);
        Assert.DoesNotMatch(@"insertAdjacentHTML\s*\(", StatusPage.Html);
        Assert.DoesNotMatch(@"document\.write\s*\(", StatusPage.Html);
    }

    /// <summary>Self-contained: a relay on a closed network still has to render.</summary>
    [Fact]
    public void ThePageLoadsNothingFromTheInternet()
    {
        Assert.DoesNotContain("http://", StatusPage.Html.Replace("http://www.w3.org/2000/svg", ""));
        Assert.DoesNotContain("https://", StatusPage.Html);
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

    /// <summary>
    /// Routing resolves these routes whatever their case, so the token gate has to as well —
    /// a case-sensitive prefix check let "/API/exec" run commands with no token at all.
    /// </summary>
    [Fact]
    public async Task MixedCasePathsAreGatedToo()
    {
        // Its own relay: these are all failed attempts, and the throttle they trip would otherwise
        // bleed into the sibling tests that share this fixture's address.
        using var relay = new IsolatedFactory();
        var http = relay.CreateClient();

        foreach (var path in new[] { "/API/exec", "/Api/Exec", "/API/command?id=x", "/API/clients", "/API/poll", "/API/upload?path=/tmp/x" })
        {
            var post = await http.PostAsync(path, new StringContent("{\"command\":\"whoami\"}", System.Text.Encoding.UTF8, "application/json"));
            var get = await http.GetAsync(path);

            // Either code means "refused"; which one depends on where the throttle kicked in.
            Assert.True(post.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests,
                $"POST {path} was not gated: {post.StatusCode}");
            Assert.True(get.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests,
                $"GET {path} was not gated: {get.StatusCode}");
        }
    }

    [Fact]
    public async Task RootPageDoesNotAdvertiseWhoIsConnected()
    {
        var body = await _http.GetStringAsync("/");

        Assert.Contains("Remote CMD Relay Server", body);
        Assert.DoesNotContain("Connected clients", body);
    }
}
