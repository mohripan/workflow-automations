using FlowForge.Contracts.Events;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.Redis;
using FlowForge.WebApi.Handlers;

namespace FlowForge.WebApi.Workers;

public class AutomationTriggeredConsumer(
    IMessageConsumer consumer,
    AutomationTriggeredHandler handler) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var @event in consumer.ConsumeAsync<AutomationTriggeredEvent>(
            StreamNames.AutomationTriggered, "webapi", "webapi-1", stoppingToken))
        {
            await handler.HandleAsync(@event, stoppingToken);
        }
    }
}
