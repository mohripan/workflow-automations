using FlowForge.Domain.Enums;

namespace FlowForge.Domain.Entities;

public class Trigger : BaseEntity<Guid>
{
    public TriggerType Type { get; private set; }
    public string ConfigJson { get; private set; } = default!;
    public Guid AutomationId { get; private set; }

    private Trigger() { }

    public static Trigger Create(TriggerType type, string configJson)
    {
        return new Trigger
        {
            Id = Guid.NewGuid(),
            Type = type,
            ConfigJson = configJson
        };
    }
}
