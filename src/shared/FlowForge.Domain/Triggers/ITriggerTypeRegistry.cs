namespace FlowForge.Domain.Triggers;

public interface ITriggerTypeRegistry
{
    void Register(ITriggerTypeDescriptor descriptor);
    ITriggerTypeDescriptor? Get(string typeId);
    IReadOnlyList<ITriggerTypeDescriptor> GetAll();
    bool IsKnown(string typeId);
}
