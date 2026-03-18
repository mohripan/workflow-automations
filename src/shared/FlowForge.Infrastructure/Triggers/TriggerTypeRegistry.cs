using System.Collections.Concurrent;
using FlowForge.Domain.Triggers;

namespace FlowForge.Infrastructure.Triggers;

public sealed class TriggerTypeRegistry : ITriggerTypeRegistry
{
    private readonly ConcurrentDictionary<string, ITriggerTypeDescriptor> _descriptors = new();

    public void Register(ITriggerTypeDescriptor descriptor)
        => _descriptors[descriptor.TypeId] = descriptor;

    public ITriggerTypeDescriptor? Get(string typeId)
        => _descriptors.GetValueOrDefault(typeId);

    public IReadOnlyList<ITriggerTypeDescriptor> GetAll()
        => [.. _descriptors.Values];

    public bool IsKnown(string typeId)
        => _descriptors.ContainsKey(typeId);
}
