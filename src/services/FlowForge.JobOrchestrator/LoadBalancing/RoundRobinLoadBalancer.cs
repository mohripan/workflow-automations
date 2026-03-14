using System.Collections.Concurrent;
using FlowForge.Domain.Entities;

namespace FlowForge.JobOrchestrator.LoadBalancing;

public class RoundRobinLoadBalancer : ILoadBalancer
{
    private readonly ConcurrentDictionary<Guid, int> _counters = new();

    public WorkflowHost Select(IReadOnlyList<WorkflowHost> hosts, Guid hostGroupId)
    {
        if (hosts.Count == 0)
            throw new InvalidOperationException($"No available hosts in group {hostGroupId}");

        var index = _counters.AddOrUpdate(
            key: hostGroupId,
            addValue: 0,
            updateValueFactory: (_, prev) => (prev + 1) % hosts.Count);

        // Ensure index is within bounds (in case list size changed)
        if (index >= hosts.Count)
        {
            index = 0;
            _counters[hostGroupId] = 0;
        }

        return hosts[index];
    }
}
