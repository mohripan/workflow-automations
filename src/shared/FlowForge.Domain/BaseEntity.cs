namespace FlowForge.Domain;

public abstract class BaseEntity<TId>
{
    public TId Id { get; protected init; } = default!;
    public DateTimeOffset CreatedAt { get; protected init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; protected set; } = DateTimeOffset.UtcNow;

    public void UpdateTimestamp() => UpdatedAt = DateTimeOffset.UtcNow;
}
