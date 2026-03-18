namespace FlowForge.Domain.Entities;

public class Trigger : BaseEntity<Guid>
{
    public Guid AutomationId { get; private set; }
    public string Name { get; private set; } = default!;
    public string TypeId { get; private set; } = default!;
    public string ConfigJson { get; private set; } = default!;

    private Trigger() { }

    public static Trigger Create(string name, string typeId, string configJson)
    {
        return new Trigger
        {
            Id = Guid.NewGuid(),
            Name = name,
            TypeId = typeId,
            ConfigJson = configJson
        };
    }
}
