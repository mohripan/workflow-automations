using FlowForge.Contracts.Events;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.JobAutomator.Evaluators;

namespace FlowForge.JobAutomator.Handlers;

public class JobStatusChangedFlagHandler(
    IEnumerable<ITriggerEvaluator> evaluators,
    ILogger<JobStatusChangedFlagHandler> logger) : IEventHandler<JobStatusChangedEvent>
{
    public async Task HandleAsync(JobStatusChangedEvent @event, CancellationToken ct)
    {
        try
        {
            var jobCompletedEvaluator = evaluators.OfType<JobCompletedTriggerEvaluator>().Single();
            await jobCompletedEvaluator.HandleJobStatusChangedAsync(@event, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing job status changed event for {JobId}", @event.JobId);
        }
    }
}
