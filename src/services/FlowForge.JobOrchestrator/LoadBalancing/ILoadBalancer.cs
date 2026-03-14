using FlowForge.Domain.Entities;

namespace FlowForge.JobOrchestrator.LoadBalancing;

public interface ILoadBalancer
{
    WorkflowHost Select(IReadOnlyList<WorkflowHost> hosts, Guid hostGroupId);
}
