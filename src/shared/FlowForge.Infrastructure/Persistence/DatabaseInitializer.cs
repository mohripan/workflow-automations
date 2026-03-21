using FlowForge.Domain.Triggers;
using FlowForge.Domain.ValueObjects;
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

        // Seed Host Groups and WorkflowHosts first, then SaveChanges so their
        // database-assigned IDs are resolved before being referenced in Automations.
        var minionGroup = FlowForge.Domain.Entities.HostGroup.Create("Minion Group", "wf-jobs-minion");
        var titanGroup = FlowForge.Domain.Entities.HostGroup.Create("Titan Group", "wf-jobs-titan");

        await context.HostGroups.AddRangeAsync(minionGroup, titanGroup);

        var minionHost = FlowForge.Domain.Entities.WorkflowHost.Create("minion", minionGroup.Id);
        var titanHost = FlowForge.Domain.Entities.WorkflowHost.Create("titan", titanGroup.Id);
        await context.WorkflowHosts.AddRangeAsync(minionHost, titanHost);

        await context.SaveChangesAsync(); // commit so minionGroup.Id / titanGroup.Id are final

        // Now build Automations using the confirmed IDs.
        const string resendTaskConfig = """
            {
              "interpreter": "/app/venv/bin/python3",
              "scriptPath": "/app/examples/send-email-resend/send_email.py",
              "packages": ["resend"],
              "env": {
                "RESEND_API_KEY": "re_2sywtsT7_9RUEfGsYSQR78XL5p3WUbeU4",
                "EMAIL_FROM": "FlowForge <noreply@kinetixflow.site>",
                "EMAIL_TO": "mohripan16@gmail.com",
                "EMAIL_SUBJECT": "FlowForge E2E Test"
              }
            }
            """;

        var emailTrigger = FlowForge.Domain.Entities.Trigger.Create(
            "every-minute",
            TriggerTypes.Schedule,
            "{\"cronExpression\": \"0 0/1 * * * ?\"}");

        var emailConditionRoot = new TriggerConditionNode(null, "every-minute", null);

        var emailAutomation = FlowForge.Domain.Entities.Automation.Create(
            "Resend E2E Email Test",
            "Sends a test email via Resend every minute to verify the full job pipeline",
            "run-script",
            minionGroup.Id,
            [emailTrigger],
            emailConditionRoot,
            taskConfig: resendTaskConfig
        );

        var sqlTrigger = FlowForge.Domain.Entities.Trigger.Create(
            "old-jobs-check",
            TriggerTypes.Sql,
            "{\"connectionString\": \"\", \"query\": \"SELECT 1 FROM jobs WHERE status = 'Completed' AND updated_at < NOW() - INTERVAL '7 days'\"}");

        var cleanupConditionRoot = new TriggerConditionNode(null, "old-jobs-check", null);

        var cleanupAutomation = FlowForge.Domain.Entities.Automation.Create(
            "Archive Old Jobs",
            "Heavy processing to archive jobs older than 7 days",
            "run-script",
            titanGroup.Id,
            [sqlTrigger],
            cleanupConditionRoot
        );

        await context.Automations.AddRangeAsync(emailAutomation, cleanupAutomation);
        await context.SaveChangesAsync();

        logger.LogInformation("Seeding completed.");
    }
}
