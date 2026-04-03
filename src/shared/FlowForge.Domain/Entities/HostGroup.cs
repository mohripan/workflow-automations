namespace FlowForge.Domain.Entities;

public class HostGroup : BaseEntity<Guid>
{
    public string Name { get; private set; } = default!;
    public string ConnectionId { get; private set; } = default!;

    private readonly List<RegistrationToken> _registrationTokens = [];
    public IReadOnlyList<RegistrationToken> RegistrationTokens => _registrationTokens.AsReadOnly();

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
    /// Creates a new registration token with TTL. Returns the raw token (show once to admin).
    /// </summary>
    public (RegistrationToken Token, string RawToken) AddRegistrationToken(TimeSpan ttl, string? label = null)
    {
        var (token, rawToken) = RegistrationToken.Create(Id, ttl, label);
        _registrationTokens.Add(token);
        UpdateTimestamp();
        return (token, rawToken);
    }

    /// <summary>
    /// Validates a raw token against all active (non-expired) tokens for this group.
    /// </summary>
    public bool ValidateRegistrationToken(string rawToken)
    {
        if (string.IsNullOrEmpty(rawToken))
            return false;

        return _registrationTokens.Any(t => t.Validate(rawToken));
    }

    /// <summary>
    /// Revokes (removes) a specific registration token by ID.
    /// </summary>
    public bool RevokeRegistrationToken(Guid tokenId)
    {
        var token = _registrationTokens.FirstOrDefault(t => t.Id == tokenId);
        if (token is null) return false;

        _registrationTokens.Remove(token);
        UpdateTimestamp();
        return true;
    }

    /// <summary>
    /// Returns count of active (non-expired) registration tokens.
    /// </summary>
    public int ActiveTokenCount => _registrationTokens.Count(t => !t.IsExpired);

    public bool HasActiveTokens => _registrationTokens.Any(t => !t.IsExpired);

    public void Update(string name)
    {
        Name = name;
        UpdateTimestamp();
    }
}

