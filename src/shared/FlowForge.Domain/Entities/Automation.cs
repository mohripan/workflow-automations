using FlowForge.Domain.Exceptions;
using FlowForge.Domain.ValueObjects;

namespace FlowForge.Domain.Entities;

public class Automation : BaseEntity<Guid>
{
    public string Name { get; private set; } = default!;
    public string? Description { get; private set; }
    public string TaskId { get; private set; } = default!;
    public Guid HostGroupId { get; private set; }
    public bool IsEnabled { get; private set; }
    public Guid? ActiveJobId { get; private set; }
    public TriggerConditionNode ConditionRoot { get; private set; } = default!;

    private List<Trigger> _triggers = [];
    public IReadOnlyList<Trigger> Triggers => _triggers;

    private Automation() { }

    public static Automation Create(
        string name,
        string? description,
        string taskId,
        Guid hostGroupId,
        List<Trigger> triggers,
        TriggerConditionNode conditionRoot)
    {
        if (triggers == null || triggers.Count == 0)
            throw new InvalidAutomationException("At least one trigger is required.");
        if (conditionRoot == null)
            throw new InvalidAutomationException("ConditionRoot must not be null.");

        ValidateConditionNames(conditionRoot, triggers.Select(t => t.Name).ToHashSet(StringComparer.Ordinal));

        return new Automation
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
            TaskId = taskId,
            HostGroupId = hostGroupId,
            IsEnabled = true,
            _triggers = triggers,
            ConditionRoot = conditionRoot
        };
    }

    public void Update(
        string name,
        string? description,
        string taskId,
        Guid hostGroupId,
        List<Trigger> triggers,
        TriggerConditionNode conditionRoot)
    {
        if (triggers == null || triggers.Count == 0)
            throw new InvalidAutomationException("At least one trigger is required.");
        if (conditionRoot == null)
            throw new InvalidAutomationException("ConditionRoot must not be null.");

        ValidateConditionNames(conditionRoot, triggers.Select(t => t.Name).ToHashSet(StringComparer.Ordinal));

        Name = name;
        Description = description;
        TaskId = taskId;
        HostGroupId = hostGroupId;
        _triggers = triggers;
        ConditionRoot = conditionRoot;
        UpdateTimestamp();
    }

    public void Enable()
    {
        IsEnabled = true;
        UpdateTimestamp();
    }

    public void Disable()
    {
        IsEnabled = false;
        UpdateTimestamp();
    }

    public void SetActiveJob(Guid jobId)
    {
        ActiveJobId = jobId;
        UpdateTimestamp();
    }

    public void ClearActiveJob()
    {
        ActiveJobId = null;
        UpdateTimestamp();
    }

    private static void ValidateConditionNames(TriggerConditionNode node, HashSet<string> knownNames)
    {
        if (node.TriggerName is not null)
        {
            if (!knownNames.Contains(node.TriggerName))
                throw new InvalidTriggerConditionException(
                    $"Condition references TriggerName '{node.TriggerName}' which is not in the triggers list.");
            return;
        }
        if (node.Nodes is not null)
            foreach (var child in node.Nodes)
                ValidateConditionNames(child, knownNames);
    }
}
