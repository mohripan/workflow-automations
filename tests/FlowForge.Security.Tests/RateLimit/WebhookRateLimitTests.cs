using FluentAssertions;
using System.Net;

namespace FlowForge.Security.Tests.RateLimit;

/// <summary>
/// Verifies the webhook rate-limit policy: 30 requests per minute per IP.
/// Sends 50 requests in a burst to guarantee the limit is exceeded regardless
/// of how many prior requests the test factory has already processed.
/// </summary>
[Collection("SecurityTests")]
public class WebhookRateLimitTests(TestWebAppFactory factory)
{
    [Fact]
    public async Task WebhookEndpoint_BurstOver30Requests_EventuallyReturns429()
    {
        var client = factory.CreateAnonymousClient();
        var id     = Guid.NewGuid(); // automation doesn't need to exist — rate limiter fires first

        var statusCodes = new List<HttpStatusCode>();

        // Send 50 requests. The FixedWindowLimiter (30/min) will reject some of them with 429.
        for (int i = 0; i < 50; i++)
        {
            var response = await client.PostAsync($"/api/automations/{id}/webhook", null);
            statusCodes.Add(response.StatusCode);
        }

        statusCodes.Should().Contain(HttpStatusCode.TooManyRequests,
            because: "the webhook rate limiter should reject requests beyond the 30/min window");
    }

    [Fact]
    public async Task WebhookEndpoint_WhenRateLimited_IncludesRetryAfterHeader()
    {
        var client = factory.CreateAnonymousClient();
        var id     = Guid.NewGuid();

        HttpResponseMessage? tooManyResponse = null;

        for (int i = 0; i < 50 && tooManyResponse is null; i++)
        {
            var response = await client.PostAsync($"/api/automations/{id}/webhook", null);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                tooManyResponse = response;
        }

        tooManyResponse.Should().NotBeNull(because: "we must hit the rate limit within 50 requests");
        tooManyResponse!.Headers.Should().ContainKey("Retry-After");
        tooManyResponse.Headers.GetValues("Retry-After").First().Should().Be("60");
    }

    [Fact]
    public async Task AuthenticatedEndpoints_BurstOver300Requests_EventuallyReturns429()
    {
        // The global limiter applies 300 req/min per user sub claim.
        // 350 requests from the same token should trigger it.
        var client = factory.CreateViewerClient();

        var statusCodes = new List<HttpStatusCode>();

        for (int i = 0; i < 350; i++)
        {
            var response = await client.GetAsync("/api/automations");
            statusCodes.Add(response.StatusCode);
        }

        statusCodes.Should().Contain(HttpStatusCode.TooManyRequests,
            because: "the global rate limiter should apply 300 req/min per authenticated user");
    }
}
