using FlowForge.Infrastructure.Messaging.Abstractions;
using StackExchange.Redis;
using System.Text.Json;

namespace FlowForge.Infrastructure.Messaging.Redis;

public class RedisStreamPublisher(IConnectionMultiplexer redis) : IMessagePublisher
{
    private readonly IDatabase _db = redis.GetDatabase();

    public async Task PublishAsync<TEvent>(TEvent @event, string? targetStream = null, CancellationToken ct = default) where TEvent : class
    {
        var streamName = targetStream ?? GetStreamName<TEvent>();
        var json = JsonSerializer.Serialize(@event);
        
        await _db.StreamAddAsync(streamName, "payload", json);
    }

    private static string GetStreamName<TEvent>() => StreamNames.ForEventType(typeof(TEvent).Name);
}
