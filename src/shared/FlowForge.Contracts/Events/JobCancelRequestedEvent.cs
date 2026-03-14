namespace FlowForge.Contracts.Events;

public record JobCancelRequestedEvent(
    Guid JobId,
    Guid HostId,
    DateTimeOffset RequestedAt
);
