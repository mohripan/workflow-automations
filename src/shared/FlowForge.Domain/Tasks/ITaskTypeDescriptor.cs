namespace FlowForge.Domain.Tasks;

public interface ITaskTypeDescriptor
{
    string TaskId { get; }
    string DisplayName { get; }
    string? Description { get; }
    IReadOnlyList<TaskParameterField> Parameters { get; }
}
