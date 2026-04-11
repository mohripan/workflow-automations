using FlowForge.Contracts.Events;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.Redis;
using FlowForge.JobAutomator.Handlers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowForge.JobAutomator.Workers;

public class JobCompletedFlagWorker(
    IMessageConsumer consumer,
    JobStatusChangedFlagHandler handler,
    ILogger<JobCompletedFlagWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("JobCompletedFlagWorker starting...");

        await foreach (var @event in consumer.ConsumeAsync<JobStatusChangedEvent>(
            StreamNames.JobStatusChanged, "job-automator-flags", "automator-1", stoppingToken))
        {
            await handler.HandleAsync(@event, stoppingToken);
        }
    }
}
