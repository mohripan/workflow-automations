using FlowForge.Infrastructure.Persistence.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FlowForge.Infrastructure.Persistence.Jobs;

public class JobsDbContextFactory : IDesignTimeDbContextFactory<JobsDbContext>
{
    public JobsDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<JobsDbContext>();
        // Use a dummy connection string for migration generation
        optionsBuilder.UseNpgsql("Host=localhost;Database=dummy;Username=postgres;Password=postgres");

        return new JobsDbContext(optionsBuilder.Options);
    }
}
