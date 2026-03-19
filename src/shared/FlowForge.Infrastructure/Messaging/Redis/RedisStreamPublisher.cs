using System.Diagnostics;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Telemetry;
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

        using var activity = FlowForgeActivitySources.Messaging.StartActivity(
            $"publish {streamName}", ActivityKind.Producer);

        await _db.StreamAddAsync(streamName,
        [
            new NameValueEntry("payload",     json),
            new NameValueEntry("traceparent", ExtractTraceparent(activity))
        ]);
    }

    private static string GetStreamName<TEvent>() => StreamNames.ForEventType(typeof(TEvent).Name);

    private static string ExtractTraceparent(Activity? activity)
    {
        if (activity is null) return string.Empty;
        var flags = activity.ActivityTraceFlags.HasFlag(ActivityTraceFlags.Recorded) ? "01" : "00";
        return $"00-{activity.TraceId}-{activity.SpanId}-{flags}";
    }
}
