using FlowForge.Domain.Enums;

namespace FlowForge.WebApi.DTOs.Requests;

public record CreateAutomationRequest(
    string Name,
    string? Description,
    Guid HostGroupId,
    string TaskId,
    List<CreateTriggerRequest> Triggers,
    TriggerConditionRequest TriggerCondition,
    int? TimeoutSeconds = null
);

public record CreateTriggerRequest(
    string Name,
    string TypeId,
    string ConfigJson
);

public record TriggerConditionRequest(
    ConditionOperator? Operator,
    string? TriggerName,
    List<TriggerConditionRequest>? Nodes
);

public record UpdateAutomationRequest(
    string Name,
    string? Description,
    Guid HostGroupId,
    string TaskId,
    List<CreateTriggerRequest> Triggers,
    TriggerConditionRequest TriggerCondition,
    int? TimeoutSeconds = null
);
