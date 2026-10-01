using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RemoteCmd.Shared;
using Xunit;

namespace RemoteCmd.Tests;

/// <summary>
/// The MCP endpoint and the OAuth flow in front of it, driven the way an MCP client (Claude Code)
/// drives them: discover, register, send the browser to authorize, wait for the dashboard, trade
/// the code with PKCE, then call tools with the bearer token.
/// </summary>
[Collection("CryptoSerial")]
public class McpTests
{
    private const string Redirect = "http://127.0.0.1:53999/callback";

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }

    /// <summary>Runs the whole sign-in and returns the access token.</summary>
    private static async Task<string> SignIn(DashboardRelay relay, HttpClient http, bool approve = true)
    {
        var reg = await http.PostAsJsonAsync("/oauth/register", new { client_name = "Test Agent", redirect_uris = new[] { Redirect } });
        Assert.Equal(HttpStatusCode.Created, reg.StatusCode);
        var clientId = (await reg.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;

        var (verifier, challenge) = Pkce();
        var page = await http.GetStringAsync(
            $"/oauth/authorize?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString(Redirect)}" +
            $"&code_challenge={challenge}&code_challenge_method=S256&state=xyz");
        var requestId = Regex.Match(page, "const id = \"([0-9a-f]+)\"").Groups[1].Value;
        Assert.NotEmpty(requestId);

        var pending = JsonDocument.Parse((await relay.Dashboard("GET", "/api/admin/oauth/pending")).Body)
            .RootElement.GetProperty("pending").EnumerateArray().Single();
        Assert.Equal("Test Agent", pending.GetProperty("client").GetString());
        Assert.Contains(pending.GetProperty("userCode").GetString()!, page); // same code on both screens

        Assert.Equal(200, (await relay.Dashboard("POST", $"/api/admin/oauth/{requestId}/{(approve ? "approve" : "deny")}")).Status);

        var status = await http.GetFromJsonAsync<JsonElement>($"/oauth/authorize/status?id={requestId}");
        var redirect = new Uri(status.GetProperty("redirect").GetString()!);
        Assert.StartsWith(Redirect, redirect.GetLeftPart(UriPartial.Path));
        Assert.Contains("state=xyz", redirect.Query);
        if (!approve)
        {
            Assert.Contains("error=access_denied", redirect.Query);
            return "";
        }

        var code = Regex.Match(redirect.Query, "code=([0-9a-f]+)").Groups[1].Value;
        var token = await http.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = clientId,
            ["redirect_uri"] = Redirect, ["code_verifier"] = verifier,
        }));
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);

        // Single use.
        var again = await http.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = clientId,
            ["redirect_uri"] = Redirect, ["code_verifier"] = verifier,
        }));
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);

        return (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
    }

    private static async Task<JsonElement> Rpc(HttpClient http, string token, string method, object? @params = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method, @params }),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var res = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task WithoutATokenTheClientIsPointedAtTheAuthorizationServer()
    {
        using var relay = new DashboardRelay();
        var http = relay.CreateClient();
        var res = await http.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "initialize" });

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var challenge = res.Headers.WwwAuthenticate.ToString();
        Assert.Contains("resource_metadata=", challenge);

        var meta = await http.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource");
        var issuer = meta.GetProperty("authorization_servers")[0].GetString();
        var server = await http.GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server");
        Assert.Equal(issuer, server.GetProperty("issuer").GetString());
        Assert.Contains("S256", server.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task AnApprovedAgentCanListAndRunCommands()
    {
        using var relay = new DashboardRelay();
        var http = relay.CreateClient();
        var token = await SignIn(relay, http);

        var init = await Rpc(http, token, "initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "t", version = "1" } });
        Assert.Equal("2025-06-18", init.GetProperty("result").GetProperty("protocolVersion").GetString());

        var tools = await Rpc(http, token, "tools/list");
        var names = tools.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("remote_exec", names);
        Assert.Contains("remote_read_file", names);

        // A paired machine that answers the command.
        var machineToken = await relay.IssueToken("client", "box");
        var machine = relay.CreateClient();
        machine.DefaultRequestHeaders.Add("X-Token", machineToken);
        var key = new CryptoKey(machineToken);
        var answering = Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
            {
                var poll = await machine.GetFromJsonAsync<JsonElement>("/api/poll");
                if (poll.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    var body = key.Encrypt(JsonSerializer.SerializeToUtf8Bytes(new { output = "ran: " + key.DecryptString(c.GetString()!), exitCode = 0 }));
                    await machine.PostAsync($"/api/result?requestId={poll.GetProperty("requestId").GetString()}", new ByteArrayContent(body));
                    return;
                }
                await Task.Delay(50);
            }
        });
        await machine.GetAsync("/api/poll"); // registers the session before the agent looks for it

        var call = await Rpc(http, token, "tools/call", new { name = "remote_exec", arguments = new { command = "hostname", client = "box" } });
        await answering;
        var result = call.GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean());
        Assert.Contains("ran: hostname", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ADeniedAgentGetsNoToken()
    {
        using var relay = new DashboardRelay();
        await SignIn(relay, relay.CreateClient(), approve: false);
        var (_, body, _) = await relay.Dashboard("GET", "/api/admin/credentials");
        Assert.DoesNotContain("MCP:", body);
    }

    [Fact]
    public void AWrongVerifierIsRefused()
    {
        Assert.True(OAuthServer.VerifierMatches("abc", Convert.ToBase64String(SHA256.HashData("abc"u8.ToArray())).TrimEnd('=').Replace('+', '-').Replace('/', '_')));
        Assert.False(OAuthServer.VerifierMatches("abd", Convert.ToBase64String(SHA256.HashData("abc"u8.ToArray())).TrimEnd('=').Replace('+', '-').Replace('/', '_')));
    }

    [Fact]
    public async Task OnlyLoopbackOrHttpsRedirectsCanRegister()
    {
        using var relay = new DashboardRelay();
        var http = relay.CreateClient();
        var bad = await http.PostAsJsonAsync("/oauth/register", new { client_name = "x", redirect_uris = new[] { "http://attacker.example/cb" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task AClientTokenCannotUseMcp()
    {
        using var relay = new DashboardRelay();
        var token = await relay.IssueToken("client", "box");
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }) };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await relay.CreateClient().SendAsync(req)).StatusCode);
    }

    /// <summary>A web page must not be able to drive the endpoint from someone's browser.</summary>
    [Fact]
    public async Task ACrossOriginRequestIsRefused()
    {
        using var relay = new DashboardRelay();
        var token = await relay.IssueToken("controller", "ci");
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "ping" }) };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await relay.CreateClient().SendAsync(req)).StatusCode);
    }
}
