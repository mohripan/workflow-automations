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

        // 3. Seed Platform Data
        await SeedPlatformDataAsync(serviceProvider, logger);
        
        logger.LogInformation("Database migration and seeding completed successfully.");
    }

    private static async Task SeedPlatformDataAsync(IServiceProvider serviceProvider, ILogger logger)
    {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        if (await context.HostGroups.AnyAsync())
        {
            return;
        }

        logger.LogInformation("Seeding initial data...");

        // Seed Host Groups
        var minionGroup = FlowForge.Domain.Entities.HostGroup.Create("Minion Group", "wf-jobs-minion");
        var titanGroup = FlowForge.Domain.Entities.HostGroup.Create("Titan Group", "wf-jobs-titan");

        await context.HostGroups.AddRangeAsync(minionGroup, titanGroup);

        // Seed sample Automation 1: Heartbeat Email (Minion)
        var emailTrigger = FlowForge.Domain.Entities.Trigger.Create(
            FlowForge.Domain.Enums.TriggerType.Schedule, 
            "{\"CronExpression\": \"0 0/5 * * * ?\"}");

        var emailAutomation = FlowForge.Domain.Entities.Automation.Create(
            "System Heartbeat Email",
            "Sends a status email every 5 minutes",
            "send-email",
            "{\"to\": \"admin@flowforge.com\", \"subject\": \"System Alive\", \"body\": \"FlowForge is running normally.\"}",
            minionGroup.Id,
            [emailTrigger],
            null // No complex condition, fires on any trigger
        );

        // Seed sample Automation 2: Data Cleanup (Titan)
        var sqlTrigger = FlowForge.Domain.Entities.Trigger.Create(
            FlowForge.Domain.Enums.TriggerType.Sql,
            "{\"Query\": \"SELECT 1 FROM jobs WHERE status = 'Completed' AND updated_at < NOW() - INTERVAL '7 days'\"}");

        var cleanupAutomation = FlowForge.Domain.Entities.Automation.Create(
            "Archive Old Jobs",
            "Heavy processing to archive jobs older than 7 days",
            "run-script",
            "{\"interpreter\": \"bash\", \"scriptPath\": \"/scripts/archive_jobs.sh\"}",
            titanGroup.Id,
            [sqlTrigger],
            null
        );

        await context.Automations.AddRangeAsync(emailAutomation, cleanupAutomation);
        await context.SaveChangesAsync();

        logger.LogInformation("Seeding completed.");
    }
}
