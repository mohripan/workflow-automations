using FlowForge.Domain.Enums;

namespace FlowForge.WebApi.DTOs.Responses;

public record AutomationResponse(
    Guid Id,
    string Name,
    string? Description,
    Guid HostGroupId,
    string TaskId,
    bool IsEnabled,
    int? TimeoutSeconds,
    int MaxRetries,
    string? TaskConfig,
    List<TriggerResponse> Triggers,
    TriggerConditionResponse TriggerCondition,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record TriggerResponse(
    Guid Id,
    string Name,
    string TypeId,
    string ConfigJson
);

public record TriggerConditionResponse(
    ConditionOperator? Operator,
    string? TriggerName,
    List<TriggerConditionResponse>? Nodes
);

public record JobResponse(
    Guid Id,
    Guid AutomationId,
    string AutomationName,
    Guid HostGroupId,
    Guid? HostId,
    JobStatus Status,
    string? Message,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record JobStatusUpdate(
    Guid JobId,
    JobStatus Status,
    string? Message,
    DateTimeOffset UpdatedAt
);
