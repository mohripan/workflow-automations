using FlowForge.Infrastructure.MultiDb;
using FlowForge.Infrastructure.Persistence.Jobs;
using FlowForge.Infrastructure.Persistence.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlowForge.Infrastructure.Persistence;

public static class DatabaseInitializer
{
    public static async Task InitializeDatabasesAsync(IServiceProvider serviceProvider)
    {
        var logger = serviceProvider.GetRequiredService<ILogger<PlatformDbContext>>();
        var config = serviceProvider.GetRequiredService<IConfiguration>();

        // 1. Migrate Platform DB
        logger.LogInformation("Migrating Platform database...");
        using (var scope = serviceProvider.CreateScope())
        {
            var platformContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await platformContext.Database.MigrateAsync();
        }

        // 2. Migrate all Job DBs
        var jobConnections = config.GetSection("JobConnections").Get<Dictionary<string, JobConnectionConfig>>() ?? [];
        
        foreach (var (connectionId, connConfig) in jobConnections)
        {
            logger.LogInformation("Migrating Jobs database for connection {ConnectionId}...", connectionId);
            
            var optionsBuilder = new DbContextOptionsBuilder<JobsDbContext>();
            optionsBuilder.UseNpgsql(connConfig.ConnectionString);
            
            using var context = new JobsDbContext(optionsBuilder.Options);
            await context.Database.MigrateAsync();
        }
        
        logger.LogInformation("Database migration completed successfully.");
    }
}
