using FlowForge.Infrastructure.Messaging.Redis;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace FlowForge.Infrastructure.Messaging.DeadLetter;

public class DlqWriter(IConnectionMultiplexer redis, ILogger<DlqWriter> logger) : IDlqWriter
{
    private readonly IDatabase _db = redis.GetDatabase();

    public async Task WriteAsync(string sourceStream, string messageId, string payload, string error, CancellationToken ct = default)
    {
        try
        {
            await _db.StreamAddAsync(StreamNames.Dlq,
            [
                new NameValueEntry("sourceStream", sourceStream),
                new NameValueEntry("messageId",    messageId),
                new NameValueEntry("payload",      payload),
                new NameValueEntry("error",        error),
                new NameValueEntry("failedAt",     DateTimeOffset.UtcNow.ToString("O"))
            ]);
        }
        catch (Exception ex)
        {
            // DLQ writes must not propagate — losing the DLQ entry is better than crashing the consumer
            logger.LogError(ex,
                "Failed to write message to DLQ. sourceStream={SourceStream}, messageId={MessageId}",
                sourceStream, messageId);
        }
    }
}
