using FlowForge.Domain.Enums;

namespace FlowForge.WebApi.DTOs.Responses;

public record AutomationResponse(
    Guid Id,
    string Name,
    string? Description,
    string TaskId,
    string DefaultParametersJson,
    Guid HostGroupId,
    List<TriggerResponse> Triggers,
    TriggerConditionResponse? TriggerCondition,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record TriggerResponse(
    Guid Id,
    TriggerType Type,
    string ConfigJson
);

public record TriggerConditionResponse(
    ConditionOperator? Operator,
    Guid? TriggerId,
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
