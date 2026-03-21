namespace FlowForge.Domain.Tasks;

public interface ITaskTypeRegistry
{
    void Register(ITaskTypeDescriptor descriptor);
    ITaskTypeDescriptor? Get(string taskId);
    IReadOnlyList<ITaskTypeDescriptor> GetAll();
    bool IsKnown(string taskId);
}
