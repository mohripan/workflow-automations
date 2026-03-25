using FlowForge.Domain.Entities;
using FlowForge.Domain.Repositories;
using FlowForge.Infrastructure.Persistence.Platform;
using Microsoft.EntityFrameworkCore;

namespace FlowForge.Infrastructure.Repositories;

public class AuditLogRepository(PlatformDbContext dbContext) : IAuditLogRepository
{
    public async Task AddAsync(AuditLog log, CancellationToken ct = default)
    {
        await dbContext.AuditLogs.AddAsync(log, ct);
        await dbContext.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AuditLog>> GetAllAsync(
        string?         entityId = null,
        DateTimeOffset? from     = null,
        DateTimeOffset? to       = null,
        CancellationToken ct     = default)
    {
        var query = dbContext.AuditLogs.AsQueryable();

        if (!string.IsNullOrWhiteSpace(entityId))
            query = query.Where(l => l.EntityId == entityId);

        if (from.HasValue)
            query = query.Where(l => l.OccurredAt >= from.Value);

        if (to.HasValue)
            query = query.Where(l => l.OccurredAt <= to.Value);

        return await query
            .OrderByDescending(l => l.OccurredAt)
            .ToListAsync(ct);
    }
}
