using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace RemoteCmd.Tests;

/// <summary>
/// The certificate must survive a restart AND actually complete a handshake. Handing Kestrel the
/// object <c>CreateSelfSigned</c> returns looks fine in every property check but fails on Windows,
/// where SChannel refuses its ephemeral key — so this test speaks real TLS to a real Kestrel.
/// </summary>
public class RelayCertificateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rcmd-cert-" + Guid.NewGuid().ToString("N")[..8]);

    public RelayCertificateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void SecondStartReusesTheSameCertificate()
    {
        var path = Path.Combine(_dir, "relay.pfx");

        var first = RelayCertificate.LoadOrCreate(path);
        var second = RelayCertificate.LoadOrCreate(path);

        Assert.Equal(first.Thumbprint, second.Thumbprint);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task CertificateCompletesATlsHandshake()
    {
        var cert = RelayCertificate.LoadOrCreate(Path.Combine(_dir, "relay.pfx"));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("https://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(o => o.ConfigureHttpsDefaults(h => h.ServerCertificate = cert));
        await using var app = builder.Build();
        app.MapGet("/", () => "pong");
        await app.StartAsync();

        var address = app.Urls.First();
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var http = new HttpClient(handler);

        Assert.Equal("pong", await http.GetStringAsync(address));

        await app.StopAsync();
    }

    [Fact]
    public async Task ReloadedCertificateAlsoCompletesAHandshake()
    {
        var path = Path.Combine(_dir, "relay.pfx");
        RelayCertificate.LoadOrCreate(path);            // first start writes it
        var reloaded = RelayCertificate.LoadOrCreate(path); // second start reads it back

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("https://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(o => o.ConfigureHttpsDefaults(h => h.ServerCertificate = reloaded));
        await using var app = builder.Build();
        app.MapGet("/", () => "pong");
        await app.StartAsync();

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var http = new HttpClient(handler);

        Assert.Equal("pong", await http.GetStringAsync(app.Urls.First()));

        await app.StopAsync();
    }
}
