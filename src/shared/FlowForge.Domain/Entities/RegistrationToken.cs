using System.Security.Cryptography;
using System.Text;

namespace FlowForge.Domain.Entities;

public class RegistrationToken : BaseEntity<Guid>
{
    public Guid HostGroupId { get; private set; }
    public string TokenHash { get; private set; } = default!;
    public string? Label { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }

    private RegistrationToken() { }

    /// <summary>
    /// Creates a new registration token. Returns the raw token (show once to admin);
    /// only the SHA-256 hash is persisted.
    /// </summary>
    public static (RegistrationToken Entity, string RawToken) Create(
        Guid hostGroupId,
        TimeSpan ttl,
        string? label = null)
    {
        var rawBytes = RandomNumberGenerator.GetBytes(32);
        var rawToken = Convert.ToBase64String(rawBytes);

        var entity = new RegistrationToken
        {
            Id = Guid.NewGuid(),
            HostGroupId = hostGroupId,
            TokenHash = HashToken(rawToken),
            Label = label,
            ExpiresAt = DateTimeOffset.UtcNow.Add(ttl),
        };

        return (entity, rawToken);
    }

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;

    public bool Validate(string rawToken)
    {
        if (IsExpired || string.IsNullOrEmpty(rawToken))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(TokenHash),
            Encoding.UTF8.GetBytes(HashToken(rawToken)));
    }

    internal static string HashToken(string rawToken)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexStringLower(hash);
    }
}
