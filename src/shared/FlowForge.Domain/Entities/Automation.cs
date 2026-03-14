using FlowForge.Domain.Enums;
using FlowForge.Domain.ValueObjects;

namespace FlowForge.Domain.Entities;

public class Automation : BaseEntity<Guid>
{
    public string Name { get; private set; } = default!;
    public string? Description { get; private set; }
    public string TaskId { get; private set; } = default!;
    public string DefaultParametersJson { get; private set; } = default!;
    public Guid HostGroupId { get; private set; }
    public List<Trigger> Triggers { get; private set; } = [];
    public TriggerCondition? TriggerCondition { get; private set; }

    private Automation() { }

    public static Automation Create(
        string name, 
        string? description, 
        string taskId,
        string defaultParametersJson,
        Guid hostGroupId, 
        List<Trigger> triggers, 
        TriggerCondition? condition)
    {
        return new Automation
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
            TaskId = taskId,
            DefaultParametersJson = defaultParametersJson,
            HostGroupId = hostGroupId,
            Triggers = triggers,
            TriggerCondition = condition
        };
    }

    public void Update(
        string name, 
        string? description, 
        string taskId,
        string defaultParametersJson,
        Guid hostGroupId, 
        List<Trigger> triggers, 
        TriggerCondition? condition)
    {
        Name = name;
        Description = description;
        TaskId = taskId;
        DefaultParametersJson = defaultParametersJson;
        HostGroupId = hostGroupId;
        Triggers = triggers;
        TriggerCondition = condition;
        UpdateTimestamp();
    }
}
