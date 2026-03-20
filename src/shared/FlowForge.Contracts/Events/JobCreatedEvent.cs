namespace FlowForge.Contracts.Events;

public record JobCreatedEvent(
    Guid JobId,
    string ConnectionId,
    Guid AutomationId,
    Guid HostGroupId,
    DateTimeOffset CreatedAt,
    int? TimeoutSeconds = null
);
