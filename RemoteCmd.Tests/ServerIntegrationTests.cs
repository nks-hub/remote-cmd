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
        // The name promises emptiness; asserting only NotNull passed just as happily with a
        // hundred sessions in the list.
        Assert.Equal(0, res.Count);
        Assert.Equal(0, res.Connected);
        Assert.Empty(res.Clients);
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

    /// <summary>
    /// A command that already timed out can still have its answer arrive. Routing that stale output
    /// to whoever happens to be waiting hands one caller another command's stdout and exit code.
    /// </summary>
    [Fact]
    public async Task LateResultOfATimedOutCommandDoesNotAnswerADifferentOne()
    {
        var id = Guid.NewGuid().ToString("N");
        var name = "late-" + Guid.NewGuid().ToString("N")[..8];
        await _http.GetAsync(Url("/api/poll", ("clientId", id), ("name", name)));

        // First command: hand it out, then let it time out without ever answering.
        var doomed = _http.PostAsJsonAsync(Url("/api/exec", ("client", name)),
            new { command = "will-time-out", timeoutSeconds = 1 });
        await Task.Delay(200);
        var first = await _http.GetFromJsonAsync<PollWithId>(Url("/api/poll", ("clientId", id), ("name", name)));
        Assert.NotNull(first?.RequestId);
        await doomed;

        // Second command, still waiting for its own answer.
        var live = _http.PostAsJsonAsync(Url("/api/exec", ("client", name)),
            new { command = "still-running", timeoutSeconds = 10 });
        await Task.Delay(200);
        var second = await _http.GetFromJsonAsync<PollWithId>(Url("/api/poll", ("clientId", id), ("name", name)));
        Assert.NotNull(second?.RequestId);
        Assert.NotEqual(first!.RequestId, second!.RequestId);

        // The dead command finally answers. It must go nowhere.
        await PostResult(id, first.RequestId!, "output-of-the-timed-out-command");
        await PostResult(id, second.RequestId!, "output-of-the-live-command");

        var body = await (await live).Content.ReadFromJsonAsync<ExecResponse>();
        Assert.Equal("output-of-the-live-command", body!.Output);
    }

    /// <summary>
    /// The client calls /api/file-done whether the write worked or not, so an upload that failed on
    /// the far end must not be reported to the uploader as a completed transfer.
    /// </summary>
    [Fact]
    public async Task UploadThatTheClientCouldNotWriteIsReportedAsAFailure()
    {
        var id = Guid.NewGuid().ToString("N");
        var name = "badwrite-" + Guid.NewGuid().ToString("N")[..8];
        await _http.GetAsync(Url("/api/poll", ("clientId", id), ("name", name)));

        using var payload = new ByteArrayContent(Encoding.UTF8.GetBytes("some file bytes"));
        var upload = _http.PostAsync(Url("/api/upload", ("path", "/read-only/nope.bin"), ("client", name)), payload);

        await Task.Delay(200);
        await _http.GetAsync(Url("/api/file-poll", ("clientId", id), ("name", name)));
        await _http.PostAsync(Url("/api/file-done", ("clientId", id), ("name", name),
            ("error", "Access to the path is denied.")), null);

        var res = await upload;
        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("denied", await res.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// History lines used to be cut at 60 characters, which truncated nearly every real command and
    /// every absolute path — the reader saw "cd /Users/x/project &amp;&amp; tar -xzf /Users/x/pro…" and had
    /// to guess the rest.
    /// </summary>
    [Fact]
    public async Task HistoryKeepsEnoughOfACommandToBeReadable()
    {
        var id = Guid.NewGuid().ToString("N");
        var name = "long-" + Guid.NewGuid().ToString("N")[..8];
        await _http.GetAsync(Url("/api/poll", ("clientId", id), ("name", name)));

        // A realistic length: two absolute paths and a couple of flags.
        var command = "cd /Users/someone/projects/mobile-app && tar -xzf /Users/someone/downloads/"
                      + "sources-2026-08-09.tgz --strip-components=1 -C ./vendor/generated && echo done";
        Assert.True(command.Length is > 100 and < 400, $"fixture is {command.Length} chars");

        await _http.PostAsJsonAsync(Url("/api/exec", ("client", name)),
            new { command, timeoutSeconds = 1 });

        var events = await _http.GetFromJsonAsync<HistoryDto>(Url("/api/events", ("limit", "200")));
        var line = events!.Events.Last(e => e.Kind == "exec" && e.Client == name);

        Assert.Equal(command, line.Message);
        Assert.DoesNotContain("…", line.Message);
    }

    private record HistoryDto(List<HistoryLine> Events);
    private record HistoryLine(string Kind, string Client, string Message);

    /// <summary>
    /// Two callers uploading to the same machine at once used to overwrite each other: the relay
    /// kept a single slot, so the client was handed one transfer's path and the other's bytes and
    /// wrote the wrong file. Each transfer now queues with its own id.
    /// </summary>
    [Fact]
    public async Task ConcurrentUploadsToOneClientDoNotSwapTheirContents()
    {
        var id = Guid.NewGuid().ToString("N");
        var name = "twofiles-" + Guid.NewGuid().ToString("N")[..8];
        await _http.GetAsync(Url("/api/poll", ("clientId", id), ("name", name)));

        using var firstBody = new ByteArrayContent(Encoding.UTF8.GetBytes("AAAA-first-file"));
        using var secondBody = new ByteArrayContent(Encoding.UTF8.GetBytes("BBBB-second-file"));
        var first = _http.PostAsync(Url("/api/upload", ("path", "/tmp/first.bin"), ("client", name)), firstBody);
        var second = _http.PostAsync(Url("/api/upload", ("path", "/tmp/second.bin"), ("client", name)), secondBody);
        await Task.Delay(300);

        // Act as the client: take one transfer at a time and check its bytes match its path.
        var seen = new Dictionary<string, string>();
        for (var i = 0; i < 2; i++)
        {
            var poll = await _http.GetFromJsonAsync<EncryptedPoll>(Url("/api/file-poll", ("clientId", id), ("name", name)));
            Assert.NotNull(poll?.E);
            var meta = System.Text.Json.JsonSerializer.Deserialize<FileMeta>(
                Crypto.DecryptString(poll!.E!),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(meta?.TransferId);

            var data = await _http.GetByteArrayAsync(Url("/api/file-data",
                ("clientId", id), ("name", name), ("transferId", meta!.TransferId!)));
            seen[meta.Path!] = Encoding.UTF8.GetString(Crypto.Decrypt(data));

            await _http.PostAsync(Url("/api/file-done",
                ("clientId", id), ("name", name), ("transferId", meta.TransferId!)), null);
        }

        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second).StatusCode);
        Assert.Equal("AAAA-first-file", seen["/tmp/first.bin"]);
        Assert.Equal("BBBB-second-file", seen["/tmp/second.bin"]);
    }

    private record EncryptedPoll(string? E);
    private record FileMeta(string? Action, string? Path, long Size, string? TransferId);

    /// <summary>
    /// The throttle has unit tests, but nothing checked that the relay actually consults it — the
    /// whole block could be deleted from the middleware and every test would stay green.
    /// </summary>
    [Fact]
    public async Task GuessingTheTokenIsThrottledEndToEnd()
    {
        using var relay = new IsolatedFactory();
        var http = relay.CreateClient();

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < AuthThrottle.MaxFailures + 3; i++)
        {
            var res = await http.GetAsync($"/api/clients?token=wrong-guess-{i}");
            codes.Add(res.StatusCode);
            if (res.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Assert.NotNull(res.Headers.RetryAfter);
                break;
            }
        }

        Assert.Contains(HttpStatusCode.Unauthorized, codes);
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
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
        TestPeer.At(builder.UseEnvironment("Testing"), System.Net.IPAddress.Loopback);
    }
}

public class IsolatedFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        TestPeer.At(builder.UseEnvironment("Testing"), System.Net.IPAddress.Loopback);
    }
}
