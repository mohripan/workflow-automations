using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace FlowForge.Infrastructure.Auth;

/// <summary>
/// Delegating handler that transparently attaches a client-credentials bearer token
/// to every outbound request. The token is cached until 30 seconds before expiry.
/// </summary>
public class ClientCredentialsHandler(IOptions<KeycloakClientOptions> options) : DelegatingHandler
{
    // One shared HttpClient for token requests — intentionally not the same client
    // that this handler decorates, so there is no circular dependency.
    private static readonly HttpClient _tokenClient = new();

    private string? _cachedToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        if (_cachedToken is null || DateTimeOffset.UtcNow >= _tokenExpiry)
            await RefreshTokenAsync(ct);

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", _cachedToken);

        return await base.SendAsync(request, ct);
    }

    private async Task RefreshTokenAsync(CancellationToken ct)
    {
        var opts = options.Value;

        var response = await _tokenClient.PostAsync(
            $"{opts.Authority}/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"]    = "client_credentials",
                ["client_id"]     = opts.ClientId,
                ["client_secret"] = opts.ClientSecret,
            }),
            ct);

        response.EnsureSuccessStatusCode();

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(ct)
            ?? throw new InvalidOperationException("Empty token response from Keycloak");

        _cachedToken = token.AccessToken;
        // Subtract 30 s so we refresh before expiry rather than after
        _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn - 30);
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")]   int    ExpiresIn);
}
