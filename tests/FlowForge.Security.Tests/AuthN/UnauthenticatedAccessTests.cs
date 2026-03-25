using FluentAssertions;
using System.Net;
using System.Net.Http.Headers;

namespace FlowForge.Security.Tests.AuthN;

/// <summary>
/// Verifies that protected endpoints reject requests with no token or with an expired / invalid token.
/// </summary>
[Collection("SecurityTests")]
public class UnauthenticatedAccessTests(TestWebAppFactory factory)
{
    private readonly HttpClient _anonymous = factory.CreateAnonymousClient();

    // ── No token → 401 ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("GET",    "/api/automations")]
    [InlineData("POST",   "/api/automations")]
    [InlineData("DELETE", "/api/automations/00000000-0000-0000-0000-000000000001")]
    [InlineData("PUT",    "/api/automations/00000000-0000-0000-0000-000000000001")]
    [InlineData("PUT",    "/api/automations/00000000-0000-0000-0000-000000000001/enable")]
    [InlineData("PUT",    "/api/automations/00000000-0000-0000-0000-000000000001/disable")]
    [InlineData("GET",    "/api/audit-logs")]
    [InlineData("GET",    "/api/dlq")]
    public async Task Request_WithNoToken_Returns401(string method, string path)
    {
        var request  = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await _anonymous.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Expired token → 401 ───────────────────────────────────────────────────

    [Fact]
    public async Task Request_WithExpiredToken_Returns401()
    {
        var client = factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokenFactory.Expired());

        var response = await client.GetAsync("/api/automations");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Invalid (garbage) token → 401 ─────────────────────────────────────────

    [Fact]
    public async Task Request_WithMalformedToken_Returns401()
    {
        var client = factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "this.is.not.a.jwt");

        var response = await client.GetAsync("/api/automations");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Anonymous-allowed endpoints ───────────────────────────────────────────

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoints_WithNoToken_Return200(string path)
    {
        var response = await _anonymous.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task WebhookFireEndpoint_WithNoToken_IsNotRejectedByAuth()
    {
        // The webhook endpoint is [AllowAnonymous]; auth rejection (401) must NOT happen.
        // The actual response depends on whether the automation exists (404 expected here).
        var id       = Guid.NewGuid();
        var response = await _anonymous.PostAsync($"/api/automations/{id}/webhook", null);

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }
}
