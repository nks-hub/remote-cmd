using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace RemoteCmd.Tests;

/// <summary>
/// The in-memory test transport has no socket, so <c>Connection.RemoteIpAddress</c> is null and
/// every request looks like it came from nowhere. Real Kestrel always knows its peer, so the tests
/// give one: without it they would exercise a state the relay never actually sees.
/// </summary>
public sealed class TestPeer(IPAddress address) : IStartupFilter
{
    public static IWebHostBuilder At(IWebHostBuilder builder, IPAddress address)
    {
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new TestPeer(address)));
        return builder;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, following) =>
        {
            context.Connection.RemoteIpAddress ??= address;
            await following();
        });
        next(app);
    };
}
