using FlowForge.Domain.Entities;

namespace FlowForge.Domain.Repositories;

public interface IAutomationRepository
{
    Task<Automation?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Automation>> GetAllAsync(CancellationToken ct = default);
    Task SaveAsync(Automation automation, CancellationToken ct = default);
    Task DeleteAsync(Automation automation, CancellationToken ct = default);
}
