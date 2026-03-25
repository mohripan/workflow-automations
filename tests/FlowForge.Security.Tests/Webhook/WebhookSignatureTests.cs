using FlowForge.Domain.Entities;
using FlowForge.Domain.ValueObjects;
using FluentAssertions;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FlowForge.Security.Tests.Webhook;

/// <summary>
/// Verifies HMAC-SHA256 signature enforcement on the webhook endpoint.
/// Automations are seeded directly via EF Core (bypassing the API) because
/// AesEncryptionService has a plain-text pass-through that allows unencrypted secrets in tests.
/// </summary>
[Collection("SecurityTests")]
public class WebhookSignatureTests(TestWebAppFactory factory) : IAsyncLifetime
{
    private const string WebhookSecret = "test-webhook-secret";
    private const string WebhookBody   = """{"event":"push","ref":"refs/heads/main"}""";

    private Guid _automationWithSecretId;
    private Guid _automationNoSecretId;

    // ── Fixture setup ─────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        await using var db = factory.CreateDbContext();

        // Automation with a webhook secret (plaintext — AES Decrypt has pass-through)
        var securedTrigger = Trigger.Create("wh", "webhook",
            JsonSerializer.Serialize(new { Secret = WebhookSecret }));

        var securedAuto = Automation.Create(
            "Webhook Secured", null, "run-script", Guid.NewGuid(),
            [securedTrigger], new TriggerConditionNode(null, "wh", null));

        // Automation without any secret — open webhook
        var openTrigger = Trigger.Create("wh", "webhook",
            JsonSerializer.Serialize(new { Secret = (string?)null }));

        var openAuto = Automation.Create(
            "Webhook Open", null, "run-script", Guid.NewGuid(),
            [openTrigger], new TriggerConditionNode(null, "wh", null));

        await db.Automations.AddRangeAsync(securedAuto, openAuto);
        await db.SaveChangesAsync();

        _automationWithSecretId = securedAuto.Id;
        _automationNoSecretId   = openAuto.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Webhook_WithCorrectHmacSignature_Returns202()
    {
        var client    = factory.CreateAnonymousClient();
        var signature = ComputeSignature(WebhookSecret, WebhookBody);

        var request = BuildWebhookRequest(_automationWithSecretId, WebhookBody, signature);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Webhook_WithWrongHmacSignature_Returns401()
    {
        var client        = factory.CreateAnonymousClient();
        var wrongSignature = ComputeSignature("completely-wrong-secret", WebhookBody);

        var request  = BuildWebhookRequest(_automationWithSecretId, WebhookBody, wrongSignature);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Webhook_WithSignatureOverTamperedBody_Returns401()
    {
        var client = factory.CreateAnonymousClient();
        // Signature computed over the original body, but a different body is sent
        var signatureOnOriginal = ComputeSignature(WebhookSecret, WebhookBody);
        const string tamperedBody = """{"event":"push","ref":"refs/heads/evil"}""";

        var request  = BuildWebhookRequest(_automationWithSecretId, tamperedBody, signatureOnOriginal);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Webhook_WithMissingSignatureHeader_Returns401()
    {
        var client   = factory.CreateAnonymousClient();
        var request  = BuildWebhookRequest(_automationWithSecretId, WebhookBody, signature: null);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Webhook_WithMalformedSignaturePrefix_Returns401()
    {
        var client  = factory.CreateAnonymousClient();
        // Uses "md5=" prefix instead of "sha256="
        var badSig  = "md5=" + Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(WebhookBody)));
        var request = BuildWebhookRequest(_automationWithSecretId, WebhookBody, badSig);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Webhook_NoSecretConfigured_AcceptsWithoutSignature()
    {
        var client   = factory.CreateAnonymousClient();
        var request  = BuildWebhookRequest(_automationNoSecretId, WebhookBody, signature: null);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Webhook_NonExistentAutomation_Returns404()
    {
        var client   = factory.CreateAnonymousClient();
        var request  = BuildWebhookRequest(Guid.NewGuid(), WebhookBody, signature: null);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ComputeSignature(string secret, string body)
    {
        var hash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexString(hash);
    }

    private static HttpRequestMessage BuildWebhookRequest(Guid id, string body, string? signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/automations/{id}/webhook")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (signature is not null)
            request.Headers.Add("X-FlowForge-Signature", signature);
        return request;
    }
}
