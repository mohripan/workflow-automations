using System.Diagnostics;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Telemetry;
using StackExchange.Redis;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace FlowForge.Infrastructure.Messaging.Redis;

public class RedisStreamConsumer(IConnectionMultiplexer redis) : IMessageConsumer
{
    private readonly IDatabase _db = redis.GetDatabase();

    public record struct StreamEntry(string Id, string Payload);

    public async IAsyncEnumerable<TEvent> ConsumeAsync<TEvent>(
        string streamName,
        string consumerGroup,
        string consumerName,
        [EnumeratorCancellation] CancellationToken ct) where TEvent : class
    {
        // Ensure consumer group exists
        try
        {
            await _db.StreamCreateConsumerGroupAsync(streamName, consumerGroup, "$", createStream: true);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
        {
            // Group already exists
        }

        while (!ct.IsCancellationRequested)
        {
            var entries = await _db.StreamReadGroupAsync(streamName, consumerGroup, consumerName, ">", count: 1);

            if (entries.Length == 0)
            {
                await Task.Delay(100, ct);
                continue;
            }

            foreach (var entry in entries)
            {
                var json          = entry.Values.FirstOrDefault(v => v.Name == "payload").Value;
                var traceparentRv = entry.Values.FirstOrDefault(v => v.Name == "traceparent").Value;

                if (!json.IsNull)
                {
                    // Restore trace context propagated from the publisher
                    ActivityContext parentContext = default;
                    if (!traceparentRv.IsNull)
                        ActivityContext.TryParse((string)traceparentRv!, null, out parentContext);

                    using var activity = FlowForgeActivitySources.Messaging.StartActivity(
                        $"consume {streamName}", ActivityKind.Consumer, parentContext);

                    var @event = JsonSerializer.Deserialize<TEvent>((string)json!);
                    if (@event != null)
                    {
                        yield return @event;
                    }
                }

                await _db.StreamAcknowledgeAsync(streamName, consumerGroup, entry.Id);
            }
        }
    }
}
