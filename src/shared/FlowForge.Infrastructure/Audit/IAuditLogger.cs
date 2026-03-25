namespace FlowForge.Infrastructure.Audit;

public interface IAuditLogger
{
    Task LogAsync(
        string            action,
        string?           entityId = null,
        object?           detail   = null,
        CancellationToken ct       = default);
}
