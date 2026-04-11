using FlowForge.Contracts.Events;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.Redis;
using FlowForge.JobOrchestrator.Handlers;

namespace FlowForge.JobOrchestrator.Workers;

public class JobDispatcherWorker(
    IMessageConsumer consumer,
    JobCreatedHandler handler) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var @event in consumer.ConsumeAsync<JobCreatedEvent>(
            StreamNames.JobCreated, "job-orchestrator", "orchestrator-1", stoppingToken))
        {
            await handler.HandleAsync(@event, stoppingToken);
        }
    }
}
