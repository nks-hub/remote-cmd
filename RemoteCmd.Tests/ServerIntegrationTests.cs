using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using RemoteCmd.Shared;
using Xunit;

namespace RemoteCmd.Tests;

/// <summary>
/// End-to-end tests that spin up the server in-process via WebApplicationFactory
/// and simulate one or more clients polling + issuing results.
/// </summary>
[Collection("CryptoSerial")]
public class ServerIntegrationTests : IClassFixture<RemoteCmdFactory>
{
    private readonly RemoteCmdFactory _factory;
    private readonly HttpClient _http;

    public ServerIntegrationTests(RemoteCmdFactory factory)
    {
        _factory = factory;
        _http = factory.CreateClient();
        Crypto.Init(RemoteCmdFactory.Token);
    }

    private string Url(string path, params (string k, string v)[] extra)
    {
        var q = $"?token={RemoteCmdFactory.Token}";
        foreach (var (k, v) in extra) q += $"&{k}={Uri.EscapeDataString(v)}";
        return path + q;
    }

    [Fact]
    public async Task InvalidToken_Returns401()
    {
        var res = await _http.GetAsync("/api/status?token=wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task NoClients_ListClients_Empty()
    {
        var fresh = _factory.WithWebHostBuilder(_ => { }).CreateClient();
        var res = await fresh.GetFromJsonAsync<ClientsResponse>(Url("/api/clients"));
        Assert.NotNull(res);
    }

    [Fact]
    public async Task Poll_RegistersClient_AndAppearsInClientsList()
    {
        var id = Guid.NewGuid().ToString("N");
        await _http.GetAsync(Url("/api/poll", ("clientId", id), ("name", "test-poll-register")));

        var list = await _http.GetFromJsonAsync<ClientsResponse>(Url("/api/clients"));
        Assert.NotNull(list);
        Assert.Contains(list!.Clients, c => c.Id == id && c.Name == "test-poll-register" && c.Connected);
    }

    [Fact]
    public async Task Exec_NoClientConnected_ReturnsErrorExitCode()
    {
        var fresh = _factory.WithWebHostBuilder(_ => { }).CreateClient();
        // Client param referencing unknown id → "Unknown client"
        var res = await fresh.PostAsJsonAsync(
            Url("/api/exec", ("client", "nonexistent")),
            new { command = "hostname", timeoutSeconds = 2 });
        var body = await res.Content.ReadFromJsonAsync<ExecResponse>();
        Assert.NotNull(body);
        Assert.Equal(-1, body!.ExitCode);
        Assert.Contains("Unknown client", body.Output);
    }

    [Fact]
    public async Task Exec_UnknownClient_ErrorIncludesAvailableNames()
    {
        Environment.SetEnvironmentVariable("REMOTECMD_TOKEN", RemoteCmdFactory.Token);
        Environment.SetEnvironmentVariable("REMOTECMD_NO_TLS", "1");
        using var iso = new IsolatedFactory();
        var http = iso.CreateClient();

        var id = Guid.NewGuid().ToString("N");
        await http.GetAsync($"/api/poll?token={RemoteCmdFactory.Token}&clientId={id}&name=available-target");

        var res = await http.PostAsJsonAsync(
            $"/api/exec?token={RemoteCmdFactory.Token}&client=does-not-exist",
            new { command = "x", timeoutSeconds = 2 });
        var body = await res.Content.ReadFromJsonAsync<ExecResponse>();
        Assert.Equal(-1, body!.ExitCode);
        Assert.Contains("Unknown client 'does-not-exist'", body.Output);
        Assert.Contains("available-target", body.Output);
    }

    [Fact]
    public async Task Poll_NameChange_ForSameClientId_PreservesSession_AndUpdatesName()
    {
        var id = Guid.NewGuid().ToString("N");
        await _http.GetAsync(Url("/api/poll", ("clientId", id), ("name", "original")));
        await _http.GetAsync(Url("/api/poll", ("clientId", id), ("name", "renamed")));

        // Both names refer to the same session; the latest poll wins
        var byOriginal = await _http.GetAsync(Url("/api/status", ("client", "original")));
        Assert.Equal(HttpStatusCode.NotFound, byOriginal.StatusCode);

        var byRenamed = await _http.GetFromJsonAsync<StatusNamed>(Url("/api/status", ("client", "renamed")));
        Assert.NotNull(byRenamed);
        Assert.Equal(id, byRenamed!.Id);
        Assert.Equal("renamed", byRenamed.Name);
    }

    [Fact]
    public async Task Exec_TargetedByName_RoundTripsThroughPoll()
    {
        var id = Guid.NewGuid().ToString("N");
        var name = "exec-single-" + Guid.NewGuid().ToString("N")[..8];

        // 1. Client polls (registers session, empty cmd)
        await _http.GetAsync(Url("/api/poll", ("clientId", id), ("name", name)));

        // 2. Controller kicks off exec (targeted by name to avoid collision with leftover sessions from other tests)
        var execTask = _http.PostAsJsonAsync(
            Url("/api/exec", ("client", name)),
            new { command = "echo hi", timeoutSeconds = 5 });

        // 3. Give the server a moment, then client polls again → receives encrypted command
        await Task.Delay(200);
        var pollRes = await _http.GetFromJsonAsync<PollDto>(Url("/api/poll", ("clientId", id), ("name", name)));
        Assert.NotNull(pollRes?.Command);
        var decrypted = Crypto.DecryptString(pollRes!.Command!);
        Assert.Equal("echo hi", decrypted);

        // 4. Client posts encrypted result
        var resultJson = System.Text.Json.JsonSerializer.Serialize(new { output = "hi", exitCode = 0 });
        var encrypted = Crypto.Encrypt(Encoding.UTF8.GetBytes(resultJson));
        using var content = new ByteArrayContent(encrypted);
        await _http.PostAsync(Url("/api/result", ("clientId", id)), content);

        // 5. Exec response arrives
        var execRes = await execTask;
        var body = await execRes.Content.ReadFromJsonAsync<ExecResponse>();
        Assert.Equal(0, body!.ExitCode);
        Assert.Equal("hi", body.Output);
    }

    /// <summary>
    /// Auto-select requires a FRESH factory (no leftover sessions from sibling tests).
    /// Own factory instance = isolated clients dictionary.
    /// </summary>
    [Fact]
    public async Task Exec_AutoSelect_WithExactlyOneConnectedClient_Works()
    {
        Environment.SetEnvironmentVariable("REMOTECMD_TOKEN", RemoteCmdFactory.Token);
        Environment.SetEnvironmentVariable("REMOTECMD_NO_TLS", "1");
        using var isolatedFactory = new IsolatedFactory();
        var http = isolatedFactory.CreateClient();

        var id = Guid.NewGuid().ToString("N");
        await http.GetAsync($"/api/poll?token={RemoteCmdFactory.Token}&clientId={id}&name=lone-client");

        var execTask = http.PostAsJsonAsync(
            $"/api/exec?token={RemoteCmdFactory.Token}",
            new { command = "auto-selected", timeoutSeconds = 5 });

        await Task.Delay(150);
        var pollRes = await http.GetFromJsonAsync<PollDto>(
            $"/api/poll?token={RemoteCmdFactory.Token}&clientId={id}&name=lone-client");
        Assert.NotNull(pollRes?.Command);
        Assert.Equal("auto-selected", Crypto.DecryptString(pollRes!.Command!));

        var resultJson = System.Text.Json.JsonSerializer.Serialize(new { output = "done", exitCode = 0 });
        var encrypted = Crypto.Encrypt(Encoding.UTF8.GetBytes(resultJson));
        using var content = new ByteArrayContent(encrypted);
        await http.PostAsync($"/api/result?token={RemoteCmdFactory.Token}&clientId={id}", content);

        var execRes = await execTask;
        var body = await execRes.Content.ReadFromJsonAsync<ExecResponse>();
        Assert.Equal(0, body!.ExitCode);
        Assert.Equal("done", body.Output);
    }

    [Fact]
    public async Task Exec_TwoClientsConnected_WithoutClientParam_ReturnsMultipleError()
    {
        var idA = Guid.NewGuid().ToString("N");
        var idB = Guid.NewGuid().ToString("N");
        await _http.GetAsync(Url("/api/poll", ("clientId", idA), ("name", "machine-a")));
        await _http.GetAsync(Url("/api/poll", ("clientId", idB), ("name", "machine-b")));

        var res = await _http.PostAsJsonAsync(
            Url("/api/exec"),
            new { command = "hostname", timeoutSeconds = 2 });
        var body = await res.Content.ReadFromJsonAsync<ExecResponse>();
        Assert.Equal(-1, body!.ExitCode);
        Assert.Contains("Multiple clients connected", body.Output);
    }

    [Fact]
    public async Task Exec_TwoClients_TargetingByName_RoutesToCorrectSession()
    {
        var idA = Guid.NewGuid().ToString("N");
        var idB = Guid.NewGuid().ToString("N");
        await _http.GetAsync(Url("/api/poll", ("clientId", idA), ("name", "alpha")));
        await _http.GetAsync(Url("/api/poll", ("clientId", idB), ("name", "beta")));

        // kick off exec for "beta"
        var execTask = _http.PostAsJsonAsync(
            Url("/api/exec", ("client", "beta")),
            new { command = "i-am-beta", timeoutSeconds = 5 });

        await Task.Delay(150);

        // Alpha polls → should see NOTHING (command was queued on beta)
        var pollA = await _http.GetFromJsonAsync<PollDto>(Url("/api/poll", ("clientId", idA), ("name", "alpha")));
        Assert.Null(pollA?.Command);

        // Beta polls → should see the encrypted command
        var pollB = await _http.GetFromJsonAsync<PollDto>(Url("/api/poll", ("clientId", idB), ("name", "beta")));
        Assert.NotNull(pollB?.Command);
        Assert.Equal("i-am-beta", Crypto.DecryptString(pollB!.Command!));

        // Beta posts result
        var resultJson = System.Text.Json.JsonSerializer.Serialize(new { output = "ok-from-beta", exitCode = 0 });
        var encrypted = Crypto.Encrypt(Encoding.UTF8.GetBytes(resultJson));
        using var content = new ByteArrayContent(encrypted);
        await _http.PostAsync(Url("/api/result", ("clientId", idB)), content);

        var execRes = await execTask;
        var body = await execRes.Content.ReadFromJsonAsync<ExecResponse>();
        Assert.Equal(0, body!.ExitCode);
        Assert.Equal("ok-from-beta", body.Output);
    }

    [Fact]
    public async Task Exec_TwoConcurrentCommands_SameClient_CorrelateByRequestId()
    {
        var id = Guid.NewGuid().ToString("N");
        var name = "concurrent-" + Guid.NewGuid().ToString("N")[..8];
        await _http.GetAsync(Url("/api/poll", ("clientId", id), ("name", name)));

        // Two execs in flight on the SAME client at once — must not serialize or cross results.
        var execA = _http.PostAsJsonAsync(Url("/api/exec", ("client", name)),
            new { command = "cmd-A", timeoutSeconds = 10 });
        var execB = _http.PostAsJsonAsync(Url("/api/exec", ("client", name)),
            new { command = "cmd-B", timeoutSeconds = 10 });

        // Drain both queued commands (one per poll), capturing each command's requestId.
        var byCommand = new Dictionary<string, string>();
        while (byCommand.Count < 2)
        {
            var p = await _http.GetFromJsonAsync<PollWithId>(Url("/api/poll", ("clientId", id), ("name", name)));
            if (p?.Command == null) { await Task.Delay(20); continue; }
            byCommand[Crypto.DecryptString(p.Command)] = p.RequestId!;
        }
        Assert.True(byCommand.ContainsKey("cmd-A"));
        Assert.True(byCommand.ContainsKey("cmd-B"));

        // Return results in REVERSE order to prove routing is by requestId, not arrival/FIFO.
        await PostResult(id, byCommand["cmd-B"], "out-B");
        await PostResult(id, byCommand["cmd-A"], "out-A");

        var bodyA = await (await execA).Content.ReadFromJsonAsync<ExecResponse>();
        var bodyB = await (await execB).Content.ReadFromJsonAsync<ExecResponse>();
        Assert.Equal("out-A", bodyA!.Output);
        Assert.Equal("out-B", bodyB!.Output);
    }

    private async Task PostResult(string clientId, string requestId, string output)
    {
        var resultJson = System.Text.Json.JsonSerializer.Serialize(new { output, exitCode = 0 });
        var encrypted = Crypto.Encrypt(Encoding.UTF8.GetBytes(resultJson));
        using var content = new ByteArrayContent(encrypted);
        await _http.PostAsync(Url("/api/result", ("clientId", clientId), ("requestId", requestId)), content);
    }

    [Fact]
    public async Task Exec_StoresItsOutput_ForTheStatusPageDetail()
    {
        var id = Guid.NewGuid().ToString("N");
        var name = "detail-" + Guid.NewGuid().ToString("N")[..8];
        await _http.GetAsync(Url("/api/poll", ("clientId", id), ("name", name)));

        var longCommand = "echo " + new string('a', 200);
        var execTask = _http.PostAsJsonAsync(
            Url("/api/exec", ("client", name)),
            new { command = longCommand, timeoutSeconds = 5 });

        await Task.Delay(200);
        var poll = await _http.GetFromJsonAsync<PollWithId>(Url("/api/poll", ("clientId", id), ("name", name)));
        Assert.NotNull(poll?.RequestId);

        var resultJson = System.Text.Json.JsonSerializer.Serialize(
            new { output = "all good\n[STDERR]\nsomething went wrong", exitCode = 3 });
        var encrypted = Crypto.Encrypt(Encoding.UTF8.GetBytes(resultJson));
        using var content = new ByteArrayContent(encrypted);
        await _http.PostAsync(Url("/api/result", ("clientId", id), ("requestId", poll!.RequestId!)), content);
        await execTask;

        var stored = await _http.GetFromJsonAsync<CommandDetail>(Url("/api/command", ("id", poll.RequestId!)));
        Assert.NotNull(stored);
        Assert.Equal(name, stored!.Client);
        Assert.Equal(longCommand, stored.Command);       // full text, not the history excerpt
        Assert.Equal("all good", stored.Stdout);
        Assert.Equal("something went wrong", stored.Stderr);
        Assert.Equal(3, stored.ExitCode);
        Assert.False(stored.Truncated);

        // The history line carries the id that links it to this record.
        var history = await _http.GetFromJsonAsync<EventsResponse>(Url("/api/events", ("limit", "200")));
        Assert.Contains(history!.Events, e => e.Kind == "exec" && e.Id == poll.RequestId);
    }

    [Theory]
    [InlineData(0, 60)]      // nothing asked for → relay default
    [InlineData(600, 600)]   // a long build is honoured, not capped at the old minute
    [InlineData(99_999, 3600)]
    public async Task Poll_HandsTheCommandsTimeoutToTheClient(int requested, int expected)
    {
        var id = Guid.NewGuid().ToString("N");
        var name = "timeout-" + Guid.NewGuid().ToString("N")[..8];
        await _http.GetAsync(Url("/api/poll", ("clientId", id), ("name", name)));

        _ = _http.PostAsJsonAsync(Url("/api/exec", ("client", name)),
            new { command = "slow-job", timeoutSeconds = requested });

        PollWithTimeout? poll = null;
        for (var i = 0; i < 50 && poll?.Command == null; i++)
        {
            poll = await _http.GetFromJsonAsync<PollWithTimeout>(Url("/api/poll", ("clientId", id), ("name", name)));
            if (poll?.Command == null) await Task.Delay(20);
        }

        Assert.NotNull(poll?.Command);
        Assert.Equal(expected, poll!.TimeoutSeconds);
    }

    [Fact]
    public async Task Command_UnknownId_Returns404()
    {
        var res = await _http.GetAsync(Url("/api/command", ("id", "no-such-command")));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Exec_TimesOut_WhenClientDoesNotRespond()
    {
        var id = Guid.NewGuid().ToString("N");
        await _http.GetAsync(Url("/api/poll", ("clientId", id), ("name", "silent")));

        var res = await _http.PostAsJsonAsync(
            Url("/api/exec", ("client", "silent")),
            new { command = "never-responded", timeoutSeconds = 1 });
        var body = await res.Content.ReadFromJsonAsync<ExecResponse>();
        Assert.Equal(-1, body!.ExitCode);
        Assert.Contains("TIMEOUT", body.Output);
    }

    [Fact]
    public async Task Status_WithoutClient_ReturnsAggregate()
    {
        var id = Guid.NewGuid().ToString("N");
        await _http.GetAsync(Url("/api/poll", ("clientId", id), ("name", "status-agg")));

        var res = await _http.GetFromJsonAsync<StatusAggregate>(Url("/api/status"));
        Assert.NotNull(res);
        Assert.True(res!.ConnectedClients >= 1);
        Assert.Equal("AES-256-GCM", res.Encryption);
    }

    [Fact]
    public async Task Status_WithClient_ReturnsDetails()
    {
        var id = Guid.NewGuid().ToString("N");
        await _http.GetAsync(Url("/api/poll", ("clientId", id), ("name", "status-named")));

        var res = await _http.GetFromJsonAsync<StatusNamed>(Url("/api/status", ("client", "status-named")));
        Assert.NotNull(res);
        Assert.True(res!.ClientConnected);
        Assert.Equal("status-named", res.Name);
    }

    [Fact]
    public async Task Status_WithUnknownClient_Returns404()
    {
        var res = await _http.GetAsync(Url("/api/status", ("client", "does-not-exist")));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // === DTOs for test deserialization ===

    record PollDto(string? Command);
    record PollWithId(string? Command, string? RequestId);
    record PollWithTimeout(string? Command, int TimeoutSeconds);
    record ExecResponse(string Output, int ExitCode);
    record CommandDetail(string Id, string Client, string Command, string Stdout, string Stderr, int ExitCode, int DurationMs, bool Truncated);
    record EventsResponse(List<EventEntry> Events);
    record EventEntry(string Kind, string Client, string Message, string? Id);
    record ClientsResponse(int Count, int Connected, List<ClientEntry> Clients);
    record ClientEntry(string Id, string Name, bool Connected);
    record StatusAggregate(bool ClientConnected, int TotalClients, int ConnectedClients, string Encryption, bool Tls);
    record StatusNamed(bool ClientConnected, string Name, string Id, string Encryption, bool Tls);
}

public class RemoteCmdFactory : WebApplicationFactory<Program>
{
    public const string Token = "integration-test-token-1234567890";

    public RemoteCmdFactory()
    {
        Environment.SetEnvironmentVariable("REMOTECMD_TOKEN", Token);
        Environment.SetEnvironmentVariable("REMOTECMD_NO_TLS", "1");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
    }
}

public class IsolatedFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
    }
}
