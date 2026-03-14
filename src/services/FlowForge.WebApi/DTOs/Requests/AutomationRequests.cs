using FlowForge.Domain.Enums;
using FlowForge.Domain.ValueObjects;

namespace FlowForge.WebApi.DTOs.Requests;

public record CreateAutomationRequest(
    string Name,
    string? Description,
    string TaskId,
    string DefaultParametersJson,
    Guid HostGroupId,
    List<CreateTriggerRequest> Triggers,
    TriggerConditionRequest TriggerCondition
);

public record CreateTriggerRequest(
    TriggerType TriggerType,
    string ConfigJson
);

public record TriggerConditionRequest(
    ConditionOperator? Operator,
    Guid? TriggerId,
    List<TriggerConditionRequest>? Nodes
);

public record UpdateAutomationRequest(
    string Name,
    string? Description,
    string TaskId,
    string DefaultParametersJson,
    Guid HostGroupId,
    List<CreateTriggerRequest> Triggers,
    TriggerConditionRequest TriggerCondition
);
