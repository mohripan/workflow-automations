using FlowForge.Domain.Entities;

namespace FlowForge.Domain.Repositories;

public interface IWorkflowHostRepository
{
    Task<WorkflowHost?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<WorkflowHost>> GetOnlineByGroupAsync(Guid hostGroupId, CancellationToken ct = default);
    Task<IReadOnlyList<WorkflowHost>> GetByGroupAsync(Guid hostGroupId, CancellationToken ct = default);
    Task<IReadOnlyList<WorkflowHost>> GetAllAsync(CancellationToken ct = default);
    Task SaveAsync(WorkflowHost host, CancellationToken ct = default);
    Task<WorkflowHost?> GetByNameAsync(string name, CancellationToken ct = default);
}
