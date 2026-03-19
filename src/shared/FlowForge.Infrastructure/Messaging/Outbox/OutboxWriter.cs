using System.Text.Json;
using FlowForge.Domain.Entities;
using FlowForge.Infrastructure.Messaging.Redis;
using FlowForge.Infrastructure.Persistence.Platform;

namespace FlowForge.Infrastructure.Messaging.Outbox;

public class OutboxWriter(PlatformDbContext db) : IOutboxWriter
{
    public Task WriteAsync<TEvent>(TEvent @event, CancellationToken ct = default) where TEvent : class
    {
        var eventType = typeof(TEvent).Name;
        var streamName = StreamNames.ForEventType(eventType);
        var payload = JsonSerializer.Serialize(@event);

        db.OutboxMessages.Add(OutboxMessage.Create(eventType, payload, streamName));

        // No SaveChangesAsync — caller commits together with the entity mutation
        return Task.CompletedTask;
    }
}
