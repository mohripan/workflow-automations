using System.Diagnostics;
using System.Text.Json;
using Dapr.Client;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowForge.Infrastructure.Messaging.Dapr;

/// <summary>
/// Publishes events via Dapr pub/sub. Each event is published as a CloudEvent
/// to the configured pub/sub component (typically backed by Kafka).
/// </summary>
public class DaprMessagePublisher(
    DaprClient daprClient,
    IOptions<DaprOptions> options,
    ILogger<DaprMessagePublisher> logger) : IMessagePublisher
{
    private readonly string _pubSubName = options.Value.PubSubName;

    public async Task PublishAsync<TEvent>(TEvent @event, string? targetTopic = null, CancellationToken ct = default)
        where TEvent : class
    {
        var topicName = targetTopic ?? TopicNames.ForEventType(typeof(TEvent).Name);

        using var activity = FlowForgeActivitySources.Messaging.StartActivity(
            $"publish {topicName}", ActivityKind.Producer);

        var metadata = BuildMetadata(activity);

        await daprClient.PublishEventAsync(_pubSubName, topicName, @event, metadata, ct);

        logger.LogDebug("Published {EventType} to Dapr topic {Topic}", typeof(TEvent).Name, topicName);
    }

    public async Task PublishRawAsync(string topic, string jsonPayload, CancellationToken ct = default)
    {
        using var activity = FlowForgeActivitySources.Messaging.StartActivity(
            $"publish {topic}", ActivityKind.Producer);

        var metadata = BuildMetadata(activity);

        // PublishEventAsync with raw content — deserialize then re-publish as object
        // to preserve CloudEvents envelope behavior
        using var doc = JsonDocument.Parse(jsonPayload);
        await daprClient.PublishEventAsync(_pubSubName, topic, doc.RootElement, metadata, ct);

        logger.LogDebug("Published raw payload to Dapr topic {Topic}", topic);
    }

    private static Dictionary<string, string> BuildMetadata(Activity? activity)
    {
        var metadata = new Dictionary<string, string>();
        if (activity is not null)
        {
            var flags = activity.ActivityTraceFlags.HasFlag(ActivityTraceFlags.Recorded) ? "01" : "00";
            metadata["traceparent"] = $"00-{activity.TraceId}-{activity.SpanId}-{flags}";
        }
        return metadata;
    }
}
