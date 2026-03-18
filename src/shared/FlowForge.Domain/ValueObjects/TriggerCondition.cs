using FlowForge.Domain.Enums;

namespace FlowForge.Domain.ValueObjects;

public record TriggerConditionNode(
    ConditionOperator? Operator,
    string? TriggerName,
    IReadOnlyList<TriggerConditionNode>? Nodes
);
