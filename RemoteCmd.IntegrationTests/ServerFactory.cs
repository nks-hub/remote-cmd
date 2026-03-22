using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

/// <summary>
/// Custom WebApplicationFactory that configures the server with a known test token
/// via environment variables and disables TLS for in-process test transport.
/// </summary>
public sealed class ServerFactory : WebApplicationFactory<Program>
{
    /// <summary>Fixed 24-char token (minimum required length) used across all integration tests.</summary>
    public const string TestToken = "integration-test-token-x";

    public ServerFactory()
    {
        Environment.SetEnvironmentVariable("REMOTECMD_TOKEN", TestToken);
        Environment.SetEnvironmentVariable("REMOTECMD_NO_TLS", "1");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
    }

    /// <summary>Creates an HttpClient with the correct Bearer token pre-set.</summary>
    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestToken);
        return client;
    }

    /// <summary>Creates an HttpClient without any Authorization header.</summary>
    public HttpClient CreateUnauthenticatedClient()
    {
        return CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
    }
}
