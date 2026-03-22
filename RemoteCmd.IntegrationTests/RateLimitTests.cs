using System.Net;

public class RateLimitTests : IClassFixture<ServerFactory>
{
    private readonly ServerFactory _factory;

    public RateLimitTests(ServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Requests_ExceedingRateLimit_Return429()
    {
        // The "general" rate limiter allows 60 req/min with QueueLimit = 0.
        // Send requests sequentially to ensure the rate limiter counts each one,
        // then fire a burst that should exceed the limit.
        var client = _factory.CreateAuthenticatedClient();

        var statusCodes = new List<HttpStatusCode>();
        for (int i = 0; i < 65; i++)
        {
            var response = await client.GetAsync("/api/status");
            statusCodes.Add(response.StatusCode);
        }

        var tooManyRequests = statusCodes.Count(s => s == HttpStatusCode.TooManyRequests);
        Assert.True(tooManyRequests > 0,
            $"Expected at least one 429 response among {statusCodes.Count} sequential requests, got none. " +
            $"Status codes: {string.Join(", ", statusCodes.Distinct().Select(s => (int)s))}");
    }
}
