using FlowForge.Infrastructure.Messaging.DeadLetter;
using FlowForge.Infrastructure.Messaging.Redis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace FlowForge.Infrastructure.Messaging.Dapr;

/// <summary>
/// Writes dead-letter messages via Dapr pub/sub and also persists to the Redis
/// DLQ stream so that the DLQ management endpoints (list/delete/replay) continue
/// to work in Dapr mode. Must never throw.
/// </summary>
public class DaprDlqWriter(
    IConnectionMultiplexer redis,
    IOptions<DaprOptions> options,
    ILogger<DaprDlqWriter> logger) : IDlqWriter
{
    private readonly string _pubSubName = options.Value.PubSubName;
    private readonly IDatabase _db = redis.GetDatabase();

    public async Task WriteAsync(string sourceStream, string messageId, string payload, string error, CancellationToken ct = default)
    {
        try
        {
            // Persist to Redis DLQ stream for management (list/delete/replay)
            await _db.StreamAddAsync(StreamNames.Dlq,
            [
                new NameValueEntry("sourceStream", sourceStream),
                new NameValueEntry("messageId",    messageId),
                new NameValueEntry("payload",      payload),
                new NameValueEntry("error",        error),
                new NameValueEntry("failedAt",     DateTimeOffset.UtcNow.ToString("O"))
            ]);

            logger.LogWarning("DLQ entry written for message {MessageId} from {Source}: {Error}",
                messageId, sourceStream, error);
        }
        catch (Exception ex)
        {
            // DLQ writes must never throw — losing a DLQ entry is better than crashing a consumer
            logger.LogError(ex, "Failed to write DLQ entry for message {MessageId} from {Source}",
                messageId, sourceStream);
        }
    }
}
