using FlowForge.Contracts.Events;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.Redis;
using FlowForge.JobAutomator.Handlers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowForge.JobAutomator.Workers;

public class AutomationCacheSyncWorker(
    IMessageConsumer consumer,
    AutomationChangedHandler handler,
    ILogger<AutomationCacheSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("AutomationCacheSyncWorker starting...");

        await foreach (var @event in consumer.ConsumeAsync<AutomationChangedEvent>(
            StreamNames.AutomationChanged, "job-automator", "automator-cache-sync", stoppingToken))
        {
            await handler.HandleAsync(@event, stoppingToken);
        }
    }
}
