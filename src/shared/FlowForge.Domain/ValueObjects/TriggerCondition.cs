using FlowForge.Domain.Enums;

namespace FlowForge.Domain.ValueObjects;

public record TriggerConditionNode(
    Guid? TriggerId,
    TriggerCondition? SubCondition
);

public record TriggerCondition(
    ConditionOperator Operator,
    IReadOnlyList<TriggerConditionNode> Nodes
);
