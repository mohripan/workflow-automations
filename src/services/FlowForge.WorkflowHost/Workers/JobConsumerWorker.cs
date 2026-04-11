using FlowForge.Contracts.Events;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.Redis;
using FlowForge.WorkflowHost.Handlers;

namespace FlowForge.WorkflowHost.Workers;

public class JobConsumerWorker(
    IMessageConsumer consumer,
    JobAssignedHandler handler) : BackgroundService
{
    private readonly string _hostId = Environment.GetEnvironmentVariable("NODE_NAME") ?? Environment.MachineName;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var @event in consumer.ConsumeAsync<JobAssignedEvent>(
            StreamNames.HostStream(_hostId), "workflow-host", _hostId, stoppingToken))
        {
            await handler.HandleAsync(@event, stoppingToken);
        }
    }

    public bool TryCancel(Guid jobId) => handler.TryCancel(jobId);
}
