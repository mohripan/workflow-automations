using FlowForge.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlowForge.Infrastructure.Persistence.Platform;

public class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public DbSet<Automation> Automations => Set<Automation>();
    public DbSet<Trigger> Triggers => Set<Trigger>();
    public DbSet<WorkflowHost> WorkflowHosts => Set<WorkflowHost>();
    public DbSet<HostGroup> HostGroups => Set<HostGroup>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(PlatformDbContext).Assembly,
            t => t.Namespace?.Contains("Persistence.Platform.Configurations") ?? false);
    }
}
