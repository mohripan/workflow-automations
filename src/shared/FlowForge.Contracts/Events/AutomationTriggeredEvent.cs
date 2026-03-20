namespace FlowForge.Contracts.Events;

public record AutomationTriggeredEvent(
    Guid AutomationId,
    Guid HostGroupId,
    string ConnectionId,
    string TaskId,
    DateTimeOffset TriggeredAt,
    int? TimeoutSeconds = null
);
