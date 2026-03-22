using System.Net;
using System.Net.Http.Headers;

public class AuthTests : IClassFixture<ServerFactory>
{
    private readonly ServerFactory _factory;

    public AuthTests(ServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Request_WithoutAuthorizationHeader_Returns401()
    {
        var client = _factory.CreateUnauthenticatedClient();

        var response = await client.GetAsync("/api/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Request_WithWrongToken_Returns401()
    {
        var client = _factory.CreateUnauthenticatedClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "wrong-token-value-here");

        var response = await client.GetAsync("/api/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Request_WithEmptyBearer_Returns401()
    {
        var client = _factory.CreateUnauthenticatedClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "");

        var response = await client.GetAsync("/api/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Request_WithCorrectToken_DoesNotReturn401()
    {
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/status");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ConstantTime_ShortToken_Returns401()
    {
        var client = _factory.CreateUnauthenticatedClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "short");

        var response = await client.GetAsync("/api/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ConstantTime_LongToken_Returns401()
    {
        var client = _factory.CreateUnauthenticatedClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "this-is-a-very-long-wrong-token-that-exceeds-normal-length-12345678");

        var response = await client.GetAsync("/api/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Request_ToNonApiPath_DoesNotRequireAuth()
    {
        var client = _factory.CreateUnauthenticatedClient();

        var response = await client.GetAsync("/");

        // Root endpoint has no auth requirement
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
