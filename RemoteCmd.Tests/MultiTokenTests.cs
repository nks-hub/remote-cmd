using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using RemoteCmd.Shared;
using Xunit;

namespace RemoteCmd.Tests;

/// <summary>
/// The relay accepts several tokens at once so clients can be rotated onto a new token without a
/// window where one of them is refused. Each client's payloads must use the key of the token that
/// client authenticated with — otherwise a rotation would silently corrupt traffic.
/// </summary>
[Collection("CryptoSerial")]
public class MultiTokenTests : IClassFixture<MultiTokenFactory>
{
    private readonly MultiTokenFactory _factory;

    public MultiTokenTests(MultiTokenFactory factory) => _factory = factory;

    [Fact]
    public async Task EitherToken_Authenticates()
    {
        var http = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/api/status?token={MultiTokenFactory.First}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/api/status?token={MultiTokenFactory.Second}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/status?token=neither-of-them")).StatusCode);
    }

    [Fact]
    public async Task CommandIsEncryptedWithThePollingClientsToken()
    {
        var http = _factory.CreateClient();
        var id = Guid.NewGuid().ToString("N");
        const string name = "second-token-client";

        // Register the client on the SECOND token.
        await http.GetAsync($"/api/poll?token={MultiTokenFactory.Second}&clientId={id}&name={name}");

        // Send the command using the FIRST token — a controller may hold a different one.
        var exec = http.PostAsJsonAsync(
            $"/api/exec?token={MultiTokenFactory.First}&client={name}",
            new { command = "whoami", timeoutSeconds = 15 });

        string? cipher = null, requestId = null;
        for (var i = 0; i < 60 && cipher is null; i++)
        {
            var poll = await http.GetFromJsonAsync<PollWithId>(
                $"/api/poll?token={MultiTokenFactory.Second}&clientId={id}&name={name}");
            cipher = poll?.Command;
            requestId = poll?.RequestId;
            if (cipher is null) await Task.Delay(50);
        }

        Assert.NotNull(cipher);
        Assert.Equal("whoami", new CryptoKey(MultiTokenFactory.Second).DecryptString(cipher!));
        Assert.ThrowsAny<Exception>(() => new CryptoKey(MultiTokenFactory.First).DecryptString(cipher!));

        var body = new CryptoKey(MultiTokenFactory.Second)
            .Encrypt(JsonSerializer.SerializeToUtf8Bytes(new { output = "root", exitCode = 0 }));
        await http.PostAsync(
            $"/api/result?token={MultiTokenFactory.Second}&clientId={id}&name={name}&requestId={requestId}",
            new ByteArrayContent(body));

        var response = await exec;
        var result = await response.Content.ReadFromJsonAsync<ExecDto>();
        Assert.Equal("root", result!.Output);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ClientListShowsWhichTokenEachClientUses()
    {
        var http = _factory.CreateClient();
        var id = Guid.NewGuid().ToString("N");
        await http.GetAsync($"/api/poll?token={MultiTokenFactory.Second}&clientId={id}&name=labelled");

        var list = await http.GetFromJsonAsync<ClientsDto>($"/api/clients?token={MultiTokenFactory.First}");
        var entry = list!.Clients.Single(c => c.Id == id);

        Assert.Equal($"{MultiTokenFactory.Second[..2]}…{MultiTokenFactory.Second[^2..]}", entry.Token);
        // The masked label must never be the token itself — that is the whole point of masking.
        Assert.DoesNotContain(MultiTokenFactory.Second, entry.Token);
        // NotNull said nothing: the field is a non-nullable string and is never null even if the
        // address were dropped entirely. The in-memory transport has no peer address, so the
        // guarantee worth testing is that the field is present and carries no token.
        Assert.NotNull(entry.Ip);
        Assert.DoesNotContain(MultiTokenFactory.Second, entry.Ip);
    }

    [Fact]
    public async Task EventsEndpointReportsActivity()
    {
        var http = _factory.CreateClient();
        var id = Guid.NewGuid().ToString("N");
        await http.GetAsync($"/api/poll?token={MultiTokenFactory.First}&clientId={id}&name=event-source");

        var info = await http.GetFromJsonAsync<EventsDto>($"/api/events?token={MultiTokenFactory.First}");

        Assert.Equal(2, info!.Tokens);
        Assert.Contains(info.Events, e => e.Kind == "connect" && e.Client == "event-source");
    }

    record PollWithId(string? Command, string? RequestId);
    record ExecDto(string Output, int ExitCode);
    record ClientsDto(int Count, List<ClientDto> Clients);
    record ClientDto(string Id, string Name, string Token, string Ip);
    record EventsDto(int Tokens, List<EventDto> Events);
    record EventDto(string Kind, string Client, string Message);
}

public class MultiTokenFactory : WebApplicationFactory<Program>
{
    public const string First = "multi-token-first-1234567890";
    public const string Second = "multi-token-second-0987654321";

    public MultiTokenFactory()
    {
        Environment.SetEnvironmentVariable("REMOTECMD_TOKENS", $"{First},{Second}");
        Environment.SetEnvironmentVariable("REMOTECMD_NO_TLS", "1");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => TestPeer.At(builder.UseEnvironment("Testing"), System.Net.IPAddress.Loopback);

    protected override void Dispose(bool disposing)
    {
        // Leave the process env as we found it: other factories in this run read the same variables.
        Environment.SetEnvironmentVariable("REMOTECMD_TOKENS", null);
        base.Dispose(disposing);
    }
}
