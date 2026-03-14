using FlowForge.Domain.Enums;

namespace FlowForge.Contracts.Events;

public record JobStatusChangedEvent(
    Guid JobId,
    string ConnectionId,
    JobStatus Status,
    string? Message,
    DateTimeOffset UpdatedAt
);
