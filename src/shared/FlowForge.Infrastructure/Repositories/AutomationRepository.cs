using FlowForge.Domain.Entities;
using FlowForge.Domain.Repositories;
using FlowForge.Infrastructure.Persistence.Platform;
using Microsoft.EntityFrameworkCore;

namespace FlowForge.Infrastructure.Repositories;

public class AutomationRepository(PlatformDbContext dbContext) : IAutomationRepository
{
    public async Task<Automation?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await dbContext.Automations
            .Include(x => x.Triggers)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<IReadOnlyList<Automation>> GetAllAsync(CancellationToken ct = default)
    {
        return await dbContext.Automations
            .Include(x => x.Triggers)
            .ToListAsync(ct);
    }

    public async Task SaveAsync(Automation automation, CancellationToken ct = default)
    {
        if (dbContext.Entry(automation).State == EntityState.Detached)
        {
            await dbContext.Automations.AddAsync(automation, ct);
        }
        await dbContext.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Automation automation, CancellationToken ct = default)
    {
        dbContext.Automations.Remove(automation);
        await dbContext.SaveChangesAsync(ct);
    }
}
