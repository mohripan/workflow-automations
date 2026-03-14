using FlowForge.Domain.Entities;
using FlowForge.Domain.Enums;
using FlowForge.Domain.Repositories;
using FlowForge.Infrastructure.Persistence.Jobs;
using Microsoft.EntityFrameworkCore;

namespace FlowForge.Infrastructure.Repositories;

public class JobRepository(JobsDbContext dbContext) : IJobRepository
{
    public async Task<Job?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await dbContext.Jobs.FindAsync([id], ct);
    }

    public async Task<IReadOnlyList<Job>> GetPendingAsync(CancellationToken ct = default)
    {
        return await dbContext.Jobs
            .Where(x => x.Status == JobStatus.Pending)
            .ToListAsync(ct);
    }

    public async Task SaveAsync(Job job, CancellationToken ct = default)
    {
        if (dbContext.Entry(job).State == EntityState.Detached)
        {
            await dbContext.Jobs.AddAsync(job, ct);
        }
        await dbContext.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Job>> GetAllAsync(Guid? automationId = null, CancellationToken ct = default)
    {
        var query = dbContext.Jobs.AsQueryable();
        if (automationId.HasValue)
        {
            query = query.Where(x => x.AutomationId == automationId.Value);
        }
        return await query.ToListAsync(ct);
    }
}
