namespace FlowForge.Domain.Triggers;

public interface ITriggerTypeDescriptor
{
    string TypeId { get; }
    string DisplayName { get; }
    string? Description { get; }
    TriggerConfigSchema GetSchema();
    IReadOnlyList<string> ValidateConfig(string configJson);
}
