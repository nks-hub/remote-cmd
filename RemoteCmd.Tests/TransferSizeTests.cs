using System.Net.Http.Json;
using RemoteCmd.Shared;
using Xunit;

namespace RemoteCmd.Tests;

/// <summary>
/// Transfers must report the bytes that actually moved: both the running counters and the history
/// line. A small file used to round down to "0 kB" and a download carried no size at all.
/// </summary>
[Collection("CryptoSerial")]
public class TransferSizeTests : IClassFixture<RemoteCmdFactory>
{
    private readonly HttpClient _http;

    public TransferSizeTests(RemoteCmdFactory factory)
    {
        _http = factory.CreateClient();
        Crypto.Init(RemoteCmdFactory.Token);
    }

    private static string Url(string path, params (string k, string v)[] extra)
    {
        var q = $"?token={RemoteCmdFactory.Token}";
        foreach (var (k, v) in extra) q += $"&{k}={Uri.EscapeDataString(v)}";
        return path + q;
    }

    private async Task<EventsDto> Events()
        => (await _http.GetFromJsonAsync<EventsDto>(Url("/api/events", ("limit", "500"))))!;

    [Fact]
    public async Task Upload_ReportsRealSize_EvenWhenItIsUnderAKilobyte()
    {
        var id = Guid.NewGuid().ToString("N");
        var name = "upload-size-" + Guid.NewGuid().ToString("N")[..8];
        await _http.GetAsync(Url("/api/poll", ("clientId", id), ("name", name)));

        var before = (await Events()).Stats.BytesUploaded;
        var payload = new byte[691];
        Random.Shared.NextBytes(payload);

        var upload = _http.PostAsync(Url("/api/upload", ("client", name), ("path", "/tmp/small.bin")),
            new ByteArrayContent(payload));

        // Play the client's side of the transfer: pick the job up, take the bytes, acknowledge.
        await DrainUpload(id, name, payload.Length);
        var res = await upload;
        res.EnsureSuccessStatusCode();

        var after = await Events();
        Assert.Equal(before + payload.Length, after.Stats.BytesUploaded);
        Assert.Contains(after.Events, e => e.Kind == "upload" && e.Client == name && e.Message.StartsWith("691 B "));
    }

    [Fact]
    public async Task Upload_OfSeveralMegabytes_ReportsMegabytes()
    {
        var id = Guid.NewGuid().ToString("N");
        var name = "upload-mb-" + Guid.NewGuid().ToString("N")[..8];
        await _http.GetAsync(Url("/api/poll", ("clientId", id), ("name", name)));

        var payload = new byte[3 * 1024 * 1024];
        var upload = _http.PostAsync(Url("/api/upload", ("client", name), ("path", "/tmp/big.bin")),
            new ByteArrayContent(payload));
        await DrainUpload(id, name, payload.Length);
        (await upload).EnsureSuccessStatusCode();

        Assert.Contains((await Events()).Events,
            e => e.Kind == "upload" && e.Client == name && e.Message.StartsWith("3 MB "));
    }

    [Fact]
    public async Task Download_ReportsTheBytesTheClientSentBack()
    {
        var id = Guid.NewGuid().ToString("N");
        var name = "download-size-" + Guid.NewGuid().ToString("N")[..8];
        await _http.GetAsync(Url("/api/poll", ("clientId", id), ("name", name)));

        var before = (await Events()).Stats.BytesDownloaded;
        var payload = new byte[2048];
        Random.Shared.NextBytes(payload);

        var download = _http.GetAsync(Url("/api/download", ("client", name), ("path", "/tmp/pull.bin")));

        // Client side: notice the job, then post the file back encrypted.
        await WaitForFileJob(id, name);
        using var body = new ByteArrayContent(Crypto.Encrypt(payload));
        await _http.PostAsync(Url("/api/file-upload", ("clientId", id), ("name", name)), body);

        var res = await download;
        res.EnsureSuccessStatusCode();
        Assert.Equal(payload.Length, (await res.Content.ReadAsByteArrayAsync()).Length);

        var after = await Events();
        Assert.Equal(before + payload.Length, after.Stats.BytesDownloaded);
        Assert.Contains(after.Events, e => e.Kind == "download" && e.Client == name && e.Message.StartsWith("2 kB "));
    }

    private async Task DrainUpload(string clientId, string name, int expectedSize)
    {
        await WaitForFileJob(clientId, name);
        var data = await _http.GetByteArrayAsync(Url("/api/file-data", ("clientId", clientId), ("name", name)));
        Assert.Equal(expectedSize, Crypto.Decrypt(data).Length);
        await _http.PostAsync(Url("/api/file-done", ("clientId", clientId), ("name", name)), null);
    }

    // Every shipped client sends its --name on every call; omitting it here would rename the session
    // out from under the transfer that targets it by name.
    private async Task WaitForFileJob(string clientId, string name)
    {
        for (var i = 0; i < 100; i++)
        {
            var poll = await _http.GetFromJsonAsync<FilePollDto>(Url("/api/file-poll", ("clientId", clientId), ("name", name)));
            if (poll?.E != null) return;
            await Task.Delay(20);
        }
        Assert.Fail("the relay never offered the transfer");
    }

    private record FilePollDto(string? E);
    private record EventsDto(StatsDto Stats, List<EventDto> Events);
    private record StatsDto(long BytesUploaded, long BytesDownloaded);
    private record EventDto(string Kind, string Client, string Message);
}
