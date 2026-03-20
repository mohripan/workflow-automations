using FlowForge.Domain.ValueObjects;

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
    bool IsEnabled,
    Guid HostGroupId,
    string ConnectionId,
    string TaskId,
    IReadOnlyList<TriggerSnapshot> Triggers,
    TriggerConditionNode ConditionRoot,
    int? TimeoutSeconds = null,
    int MaxRetries = 0
);

public record TriggerSnapshot(
    Guid Id,
    string Name,
    string TypeId,
    string ConfigJson
);
