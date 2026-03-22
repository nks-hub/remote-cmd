using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

public class ExecTests : IClassFixture<ServerFactory>
{
    private readonly ServerFactory _factory;

    public ExecTests(ServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PostExec_WithoutBody_Returns4xx()
    {
        var client = _factory.CreateAuthenticatedClient();
        // Sending empty body causes ReadFromJsonAsync to throw JsonException,
        // which ASP.NET Core surfaces as 400 BadRequest in minimal APIs.
        var content = new StringContent("", Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/exec", content);

        Assert.True(
            (int)response.StatusCode >= 400,
            $"Expected 4xx but got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task PostExec_WithNullCommand_ReturnsBadRequest()
    {
        var client = _factory.CreateAuthenticatedClient();
        var body = new { command = (string?)null };

        var response = await client.PostAsync("/api/exec", JsonContent.Create(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("error", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostExec_WithEmptyStringCommand_ReturnsErrorResponse()
    {
        // Empty string passes the null-check in Program.cs but no client is connected.
        // The server returns 200 OK with an [ERROR] output — this is the correct behavior.
        var client = _factory.CreateAuthenticatedClient();
        var body = new { command = "" };

        var response = await client.PostAsync("/api/exec", JsonContent.Create(body));

        // Server either rejects empty command (400) or returns an error result (200 with [ERROR])
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var output = json.GetProperty("output").GetString() ?? "";
            Assert.True(
                output.StartsWith("[ERROR]", StringComparison.Ordinal) || json.GetProperty("exitCode").GetInt32() != 0,
                $"Expected error output for empty command, got: {output}");
        }
        else
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task GetStatus_ReturnsJson_WithExpectedFields()
    {
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/status");

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(json.TryGetProperty("clientConnected", out _), "Missing 'clientConnected' field");
        Assert.True(json.TryGetProperty("encryption", out var enc), "Missing 'encryption' field");
        Assert.Equal("AES-256-GCM", enc.GetString());
        Assert.True(json.TryGetProperty("tls", out _), "Missing 'tls' field");
        Assert.True(json.TryGetProperty("secondsAgo", out _), "Missing 'secondsAgo' field");
    }

    [Fact]
    public async Task GetStatus_TlsField_IsFalse_WhenNoTlsConfigured()
    {
        // ServerFactory sets REMOTECMD_NO_TLS=1, so tls should be false
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/status");

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("tls").GetBoolean());
    }

    [Fact]
    public async Task PostExec_WhenNoClientConnected_ReturnsErrorOutput()
    {
        var client = _factory.CreateAuthenticatedClient();
        var body = new { command = "echo hello", timeoutSeconds = 2 };

        var response = await client.PostAsync("/api/exec", JsonContent.Create(body));

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var output = json.GetProperty("output").GetString() ?? "";
        Assert.Contains("[ERROR]", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostExec_ConcurrentRequests_SecondGetsAnotherCommandPending()
    {
        // Simulate a "connected" client by hitting /api/poll to update LastClientPoll
        var clientA = _factory.CreateAuthenticatedClient();
        await clientA.GetAsync("/api/poll");

        // Fire two concurrent exec requests; since the client never returns a result,
        // the second should find the lock held after a 2-second WaitAsync timeout.
        var body1 = new { command = "sleep 5", timeoutSeconds = 5 };
        var body2 = new { command = "echo second", timeoutSeconds = 5 };

        var t1 = clientA.PostAsync("/api/exec", JsonContent.Create(body1));
        await Task.Delay(100); // ensure t1 acquires the semaphore first
        var t2 = clientA.PostAsync("/api/exec", JsonContent.Create(body2));

        var responses = await Task.WhenAll(t1, t2);
        var bodies = await Task.WhenAll(
            responses[0].Content.ReadAsStringAsync(),
            responses[1].Content.ReadAsStringAsync());

        var combined = bodies[0] + bodies[1];
        Assert.Contains("Another command is pending", combined, StringComparison.Ordinal);
    }
}
