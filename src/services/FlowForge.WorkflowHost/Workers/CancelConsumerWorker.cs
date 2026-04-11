using FlowForge.Contracts.Events;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.Redis;
using FlowForge.WorkflowHost.Handlers;

namespace FlowForge.WorkflowHost.Workers;

public class CancelConsumerWorker(
    IMessageConsumer consumer,
    JobCancelRequestedHandler handler) : BackgroundService
{
    private readonly string _hostId = Environment.GetEnvironmentVariable("NODE_NAME") ?? Environment.MachineName;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {

        await foreach (var @event in consumer.ConsumeAsync<JobCancelRequestedEvent>(
            StreamNames.JobCancelRequested, "workflow-host", _hostId, stoppingToken))
        {
            await handler.HandleAsync(@event, stoppingToken);
        }
    }
}
