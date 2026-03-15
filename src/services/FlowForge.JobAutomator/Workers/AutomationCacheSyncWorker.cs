using FlowForge.Contracts.Events;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.Redis;
using FlowForge.JobAutomator.Cache;
using FlowForge.JobAutomator.Initialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowForge.JobAutomator.Workers;

public class AutomationCacheSyncWorker(
    IMessageConsumer consumer,
    AutomationCache cache,
    IQuartzScheduleSync scheduleSync,
    ILogger<AutomationCacheSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("AutomationCacheSyncWorker starting...");

        await foreach (var @event in consumer.ConsumeAsync<AutomationChangedEvent>(
            "flowforge:automation-changed", "job-automator", "automator-1", stoppingToken))
        {
            try
            {
                switch (@event.ChangeType)
                {
                    case ChangeType.Created:
                    case ChangeType.Updated:
                        if (@event.Automation != null)
                        {
                            cache.Upsert(@event.Automation);
                            await scheduleSync.SyncAsync(@event.Automation, stoppingToken);
                            logger.LogInformation("Cache updated for automation {AutomationId}", @event.AutomationId);
                        }
                        break;
                    case ChangeType.Deleted:
                        cache.Remove(@event.AutomationId);
                        await scheduleSync.RemoveAsync(@event.AutomationId, stoppingToken);
                        logger.LogInformation("Automation {AutomationId} removed from cache", @event.AutomationId);
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing automation changed event for {AutomationId}", @event.AutomationId);
            }
        }
    }
}
