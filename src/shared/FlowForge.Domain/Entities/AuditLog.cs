namespace FlowForge.Domain.Entities;

public class AuditLog
{
    public Guid    Id          { get; private set; }
    public string  Action      { get; private set; } = default!;
    public string? EntityId    { get; private set; }
    public string? UserId      { get; private set; }
    public string? Username    { get; private set; }
    public string? Detail      { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }

    private AuditLog() { }

    public static AuditLog Create(
        string  action,
        string? entityId  = null,
        string? userId    = null,
        string? username  = null,
        string? detail    = null)
        => new()
        {
            Id         = Guid.NewGuid(),
            Action     = action,
            EntityId   = entityId,
            UserId     = userId,
            Username   = username,
            Detail     = detail,
            OccurredAt = DateTimeOffset.UtcNow,
        };
}
