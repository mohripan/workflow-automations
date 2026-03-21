using System.Collections.Concurrent;
using FlowForge.Domain.Tasks;

namespace FlowForge.Infrastructure.Tasks;

public sealed class TaskTypeRegistry : ITaskTypeRegistry
{
    private readonly ConcurrentDictionary<string, ITaskTypeDescriptor> _descriptors = new();

    public void Register(ITaskTypeDescriptor descriptor)
        => _descriptors[descriptor.TaskId] = descriptor;

    public ITaskTypeDescriptor? Get(string taskId)
        => _descriptors.GetValueOrDefault(taskId);

    public IReadOnlyList<ITaskTypeDescriptor> GetAll()
        => [.. _descriptors.Values];

    public bool IsKnown(string taskId)
        => _descriptors.ContainsKey(taskId);
}
