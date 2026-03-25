using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowForge.WebApi.Auth;

/// <summary>
/// Extracts realm roles from the Keycloak <c>realm_access.roles</c> JWT claim
/// and maps them to <see cref="ClaimTypes.Role"/> so that
/// <c>[Authorize(Roles = "admin")]</c> and policy role checks work out of the box.
/// </summary>
public class KeycloakRolesClaimsTransformation : IClaimsTransformation
{
    private sealed record RealmAccess(
        [property: JsonPropertyName("roles")] string[] Roles);

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var identity = (ClaimsIdentity)principal.Identity!;
        var realmAccess = principal.FindFirst("realm_access")?.Value;
        if (realmAccess is null)
            return Task.FromResult(principal);

        var access = JsonSerializer.Deserialize<RealmAccess>(realmAccess);
        foreach (var role in access?.Roles ?? [])
            identity.AddClaim(new Claim(ClaimTypes.Role, role));

        return Task.FromResult(principal);
    }
}
