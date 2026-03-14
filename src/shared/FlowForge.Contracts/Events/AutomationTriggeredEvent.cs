namespace FlowForge.Contracts.Events;

public record AutomationTriggeredEvent(
    Guid AutomationId,
    Guid HostGroupId,
    DateTimeOffset TriggeredAt
);
