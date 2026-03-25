using FlowForge.Domain.Entities;

namespace FlowForge.Domain.Repositories;

public interface IAuditLogRepository
{
    Task AddAsync(AuditLog log, CancellationToken ct = default);

    Task<IReadOnlyList<AuditLog>> GetAllAsync(
        string?         entityId = null,
        DateTimeOffset? from     = null,
        DateTimeOffset? to       = null,
        CancellationToken ct     = default);
}
