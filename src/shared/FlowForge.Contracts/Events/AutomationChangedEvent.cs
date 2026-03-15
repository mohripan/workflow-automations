using FlowForge.Domain.Enums;

namespace FlowForge.Contracts.Events;

public record AutomationChangedEvent(
    Guid AutomationId,
    ChangeType ChangeType,
    AutomationSnapshot? Automation
);

public enum ChangeType { Created, Updated, Deleted }

public record AutomationSnapshot(
    Guid Id,
    string Name,
    bool IsActive,
    Guid HostGroupId,
    string ConnectionId,
    string TaskId,
    IReadOnlyList<TriggerSnapshot> Triggers,
    TriggerConditionSnapshot? ConditionRoot
);

public record TriggerSnapshot(
    Guid Id,
    TriggerType Type,
    string ConfigJson
);

public enum ConditionOperator { And, Or }

public record TriggerConditionSnapshot(
    ConditionOperator? Operator,
    Guid? TriggerId,
    IReadOnlyList<TriggerConditionSnapshot>? Nodes
);
