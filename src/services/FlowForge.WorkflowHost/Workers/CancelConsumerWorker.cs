using FlowForge.Contracts.Events;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.Redis;

namespace FlowForge.WorkflowHost.Workers;

public class CancelConsumerWorker(
    IMessageConsumer consumer,
    IEnumerable<IHostedService> hostedServices,
    ILogger<CancelConsumerWorker> logger) : BackgroundService
{
    private readonly string _hostId = Environment.GetEnvironmentVariable("NODE_NAME") ?? Environment.MachineName;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var jobConsumer = hostedServices.OfType<JobConsumerWorker>().Single();

        await foreach (var @event in consumer.ConsumeAsync<JobCancelRequestedEvent>(
            StreamNames.JobCancelRequested, "workflow-host", _hostId, stoppingToken))
        {
            if (@event.HostId.ToString() == _hostId || @event.HostId == Guid.Empty)
            {
                if (jobConsumer.TryCancel(@event.JobId))
                {
                    logger.LogInformation("Successfully cancelled job {JobId}", @event.JobId);
                }
                else
                {
                    logger.LogDebug("Received cancel request for job {JobId} but it's not active on this host", @event.JobId);
                }
            }
        }
    }
}
