using System.Diagnostics;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Telemetry;
using StackExchange.Redis;
using System.Text.Json;

namespace FlowForge.Infrastructure.Messaging.Redis;

public class RedisStreamPublisher(IConnectionMultiplexer redis) : IMessagePublisher
{
    private readonly IDatabase _db = redis.GetDatabase();

    public async Task PublishAsync<TEvent>(TEvent @event, string? targetTopic = null, CancellationToken ct = default) where TEvent : class
    {
        var streamName = targetTopic is not null
            ? ToStreamName(targetTopic)
            : GetStreamName<TEvent>();
        var json = JsonSerializer.Serialize(@event);

        using var activity = FlowForgeActivitySources.Messaging.StartActivity(
            $"publish {streamName}", ActivityKind.Producer);

        await _db.StreamAddAsync(streamName,
        [
            new NameValueEntry("payload",     json),
            new NameValueEntry("traceparent", ExtractTraceparent(activity))
        ]);
    }

    public async Task PublishRawAsync(string topic, string jsonPayload, CancellationToken ct = default)
    {
        var streamName = ToStreamName(topic);

        using var activity = FlowForgeActivitySources.Messaging.StartActivity(
            $"publish {streamName}", ActivityKind.Producer);

        await _db.StreamAddAsync(streamName,
        [
            new NameValueEntry("payload",     jsonPayload),
            new NameValueEntry("traceparent", ExtractTraceparent(activity))
        ]);
    }

    private static string GetStreamName<TEvent>() =>
        StreamNames.ForEventType(typeof(TEvent).Name);

    /// <summary>
    /// Converts a provider-agnostic topic name to a Redis stream name.
    /// If the topic already uses the "flowforge:" prefix, it is returned as-is.
    /// </summary>
    private static string ToStreamName(string topic) =>
        topic.StartsWith("flowforge:", StringComparison.Ordinal) ? topic : $"flowforge:{topic}";

    private static string ExtractTraceparent(Activity? activity)
    {
        if (activity is null) return string.Empty;
        var flags = activity.ActivityTraceFlags.HasFlag(ActivityTraceFlags.Recorded) ? "01" : "00";
        return $"00-{activity.TraceId}-{activity.SpanId}-{flags}";
    }
}
