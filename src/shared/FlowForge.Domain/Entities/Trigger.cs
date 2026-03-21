namespace FlowForge.Domain.Entities;

public class Trigger : BaseEntity<Guid>
{
    // Explicit backing field so EF Core's field-naming convention (_automationId)
    // can locate and write it during FK fixup. The compiler-generated
    // <AutomationId>k__BackingField name is invisible to EF Core.
    private Guid _automationId = Guid.Empty;
    public Guid AutomationId => _automationId;
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
