using FlowForge.Contracts.Events;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.Redis;
using FlowForge.JobAutomator.Evaluators;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowForge.JobAutomator.Workers;

public class JobCompletedFlagWorker(
    IMessageConsumer consumer,
    IEnumerable<ITriggerEvaluator> evaluators,
    ILogger<JobCompletedFlagWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("JobCompletedFlagWorker starting...");

        var jobCompletedEvaluator = evaluators.OfType<JobCompletedTriggerEvaluator>().Single();

        await foreach (var @event in consumer.ConsumeAsync<JobStatusChangedEvent>(
            StreamNames.JobStatusChanged, "job-automator-flags", "automator-1", stoppingToken))
        {
            try
            {
                await jobCompletedEvaluator.HandleJobStatusChangedAsync(@event, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing job status changed event for {JobId}", @event.JobId);
            }
        }
    }
}
