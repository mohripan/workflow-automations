using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;

namespace FlowForge.Security.Tests;

/// <summary>
/// Generates self-signed JWTs for security tests.
/// All tokens are signed with a test RSA key — no Keycloak is needed.
/// </summary>
public static class TestTokenFactory
{
    public const string Issuer   = "test-issuer";
    public const string Audience = "flowforge-webapi";

    // Single RSA key shared across all tokens in the test run
    private static readonly RSA        _rsa = RSA.Create(2048);
    public  static readonly RsaSecurityKey Key = new(_rsa);

    public static string ForAdmin(string userId = "admin-user")
        => Create(userId, ["admin"]);

    public static string ForOperator(string userId = "operator-user")
        => Create(userId, ["operator"]);

    public static string ForViewer(string userId = "viewer-user")
        => Create(userId, ["viewer"]);

    /// <summary>Creates a token already past its expiry.</summary>
    public static string Expired()
    {
        var claims = BuildClaims("expired-user", []);
        var creds  = new SigningCredentials(Key, SecurityAlgorithms.RsaSha256);
        var token  = new JwtSecurityToken(
            issuer:             Issuer,
            audience:           Audience,
            claims:             claims,
            notBefore:          DateTime.UtcNow.AddMinutes(-10),
            expires:            DateTime.UtcNow.AddMinutes(-1),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string Create(string userId, string[] roles)
    {
        var creds = new SigningCredentials(Key, SecurityAlgorithms.RsaSha256);
        var token = new JwtSecurityToken(
            issuer:             Issuer,
            audience:           Audience,
            claims:             BuildClaims(userId, roles),
            expires:            DateTime.UtcNow.AddMinutes(30),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static IEnumerable<Claim> BuildClaims(string userId, string[] roles) =>
    [
        new Claim(JwtRegisteredClaimNames.Sub, userId),
        new Claim("preferred_username", userId),
        new Claim("realm_access", JsonSerializer.Serialize(new { roles })),
    ];
}
