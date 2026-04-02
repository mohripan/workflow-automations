using FlowForge.Domain.Entities;
using FlowForge.Domain.Repositories;
using FlowForge.Infrastructure.Persistence.Platform;
using Microsoft.EntityFrameworkCore;

namespace FlowForge.Infrastructure.Repositories;

public class HostGroupRepository(PlatformDbContext dbContext) : IHostGroupRepository
{
    public async Task<HostGroup?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await dbContext.HostGroups.FindAsync([id], ct);
    }

    public async Task<IReadOnlyList<HostGroup>> GetAllAsync(CancellationToken ct = default)
    {
        return await dbContext.HostGroups.ToListAsync(ct);
    }

    public async Task SaveAsync(HostGroup hostGroup, CancellationToken ct = default)
    {
        if (dbContext.Entry(hostGroup).State == EntityState.Detached)
        {
            await dbContext.HostGroups.AddAsync(hostGroup, ct);
        }
        await dbContext.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(HostGroup hostGroup, CancellationToken ct = default)
    {
        dbContext.HostGroups.Remove(hostGroup);
        await dbContext.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<HostGroup>> GetAllWithTokenAsync(CancellationToken ct = default)
    {
        return await dbContext.HostGroups
            .Where(g => g.RegistrationTokenHash != null)
            .ToListAsync(ct);
    }
}
