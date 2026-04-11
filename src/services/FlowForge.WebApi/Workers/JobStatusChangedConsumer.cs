using FlowForge.Contracts.Events;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.Redis;
using FlowForge.WebApi.Handlers;

namespace FlowForge.WebApi.Workers;

public class JobStatusChangedConsumer(
    IMessageConsumer consumer,
    JobStatusChangedHandler handler) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var @event in consumer.ConsumeAsync<JobStatusChangedEvent>(
            StreamNames.JobStatusChanged, "webapi", "webapi-1", stoppingToken))
        {
            await handler.HandleAsync(@event, stoppingToken);
        }
    }
}
