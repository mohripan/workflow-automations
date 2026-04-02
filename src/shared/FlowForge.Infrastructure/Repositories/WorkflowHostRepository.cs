using FlowForge.Domain.Entities;
using FlowForge.Domain.Repositories;
using FlowForge.Infrastructure.Persistence.Platform;
using Microsoft.EntityFrameworkCore;

namespace FlowForge.Infrastructure.Repositories;

public class WorkflowHostRepository(PlatformDbContext dbContext) : IWorkflowHostRepository
{
    public async Task<WorkflowHost?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await dbContext.WorkflowHosts.FindAsync([id], ct);
    }

    public async Task<IReadOnlyList<WorkflowHost>> GetOnlineByGroupAsync(Guid hostGroupId, CancellationToken ct = default)
    {
        return await dbContext.WorkflowHosts
            .Where(h => h.HostGroupId == hostGroupId && h.IsOnline)
            .ToListAsync(ct);
    }

    public async Task SaveAsync(WorkflowHost host, CancellationToken ct = default)
    {
        var existing = await dbContext.WorkflowHosts.FindAsync([host.Id], ct);
        if (existing == null)
        {
            await dbContext.WorkflowHosts.AddAsync(host, ct);
        }
        else
        {
            dbContext.Entry(existing).CurrentValues.SetValues(host);
        }
        await dbContext.SaveChangesAsync(ct);
    }

    public async Task<WorkflowHost?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        return await dbContext.WorkflowHosts.FirstOrDefaultAsync(h => h.Name == name, ct);
    }

    public async Task<IReadOnlyList<WorkflowHost>> GetByGroupAsync(Guid hostGroupId, CancellationToken ct = default)
    {
        return await dbContext.WorkflowHosts
            .Where(h => h.HostGroupId == hostGroupId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<WorkflowHost>> GetAllAsync(CancellationToken ct = default)
    {
        return await dbContext.WorkflowHosts.ToListAsync(ct);
    }

    public async Task DeleteAsync(WorkflowHost host, CancellationToken ct = default)
    {
        dbContext.WorkflowHosts.Remove(host);
        await dbContext.SaveChangesAsync(ct);
    }
}
