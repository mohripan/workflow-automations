namespace FlowForge.Contracts.Events;

public record JobAssignedEvent(
    Guid JobId,
    string ConnectionId,
    Guid HostId,
    Guid AutomationId,
    DateTimeOffset AssignedAt
);
