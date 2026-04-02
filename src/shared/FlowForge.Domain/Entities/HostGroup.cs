using System.Security.Cryptography;
using System.Text;

namespace FlowForge.Domain.Entities;

public class HostGroup : BaseEntity<Guid>
{
    public string Name { get; private set; } = default!;
    public string ConnectionId { get; private set; } = default!;
    public string? RegistrationTokenHash { get; private set; }

    private HostGroup() { }

    public static HostGroup Create(string name, string connectionId)
    {
        return new HostGroup
        {
            Id = Guid.NewGuid(),
            Name = name,
            ConnectionId = connectionId
        };
    }

    /// <summary>
    /// Generates a new registration token for agent enrollment.
    /// Returns the raw token (show once to admin); only the SHA-256 hash is persisted.
    /// </summary>
    public string GenerateRegistrationToken()
    {
        var rawBytes = RandomNumberGenerator.GetBytes(32);
        var rawToken = Convert.ToBase64String(rawBytes);
        RegistrationTokenHash = HashToken(rawToken);
        UpdateTimestamp();
        return rawToken;
    }

    public bool ValidateRegistrationToken(string rawToken)
    {
        if (string.IsNullOrEmpty(RegistrationTokenHash) || string.IsNullOrEmpty(rawToken))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(RegistrationTokenHash),
            Encoding.UTF8.GetBytes(HashToken(rawToken)));
    }

    public void RevokeRegistrationToken()
    {
        RegistrationTokenHash = null;
        UpdateTimestamp();
    }

    public void Update(string name)
    {
        Name = name;
        UpdateTimestamp();
    }

    internal static string HashToken(string rawToken)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexStringLower(hash);
    }
}
