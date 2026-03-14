using FlowForge.Domain.Entities;

namespace FlowForge.Domain.Repositories;

public interface IJobRepository
{
    Task<Job?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Job>> GetPendingAsync(CancellationToken ct = default);
    Task SaveAsync(Job job, CancellationToken ct = default);
    Task<IReadOnlyList<Job>> GetAllAsync(Guid? automationId = null, CancellationToken ct = default);
}
