using FlowForge.Domain.Entities;

namespace FlowForge.Domain.Repositories;

public interface IHostGroupRepository
{
    Task<HostGroup?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<HostGroup?> GetByIdWithTokensAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<HostGroup>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<HostGroup>> GetAllWithTokensAsync(CancellationToken ct = default);
    Task SaveAsync(HostGroup hostGroup, CancellationToken ct = default);
    Task DeleteAsync(HostGroup hostGroup, CancellationToken ct = default);
}
