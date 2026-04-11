using FlowForge.Infrastructure.Messaging.Redis;
using StackExchange.Redis;

namespace FlowForge.Infrastructure.Messaging.DeadLetter;

/// <summary>
/// DLQ reader for Dapr mode. Since DLQ entries in Dapr mode are also written
/// to the Redis <c>flowforge:dlq</c> stream (via <see cref="DaprDlqWriter"/>
/// which internally persists to Redis), this delegates to the same Redis store.
/// This ensures DLQ management (list/delete/replay) works identically in both modes.
/// </summary>
public class DaprDlqReader(
    IConnectionMultiplexer redis,
    Abstractions.IMessagePublisher publisher) : IDlqReader
{
    private readonly IDatabase _db = redis.GetDatabase();

    public async Task<IReadOnlyList<DlqEntry>> GetEntriesAsync(int limit = 50, CancellationToken ct = default)
    {
        var entries = await _db.StreamRangeAsync(StreamNames.Dlq, "-", "+", count: limit);
        return entries.Select(ParseEntry).ToList();
    }

    public async Task<DlqEntry?> GetEntryAsync(string id, CancellationToken ct = default)
    {
        var entries = await _db.StreamRangeAsync(StreamNames.Dlq, id, id, count: 1);
        return entries.Length == 0 ? null : ParseEntry(entries[0]);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        var deleted = await _db.StreamDeleteAsync(StreamNames.Dlq, [new RedisValue(id)]);
        return deleted > 0;
    }

    public async Task<bool> ReplayAsync(string id, CancellationToken ct = default)
    {
        var entry = await GetEntryAsync(id, ct);
        if (entry is null) return false;

        if (string.IsNullOrEmpty(entry.SourceStream) || string.IsNullOrEmpty(entry.Payload))
            return false;

        // Replay via Dapr pub/sub to the original topic
        await publisher.PublishRawAsync(entry.SourceStream, entry.Payload, ct);
        return true;
    }

    private static DlqEntry ParseEntry(StreamEntry e) => new(
        Id:           e.Id!,
        SourceStream: (string?)e.Values.FirstOrDefault(v => v.Name == "sourceStream").Value ?? "",
        MessageId:    (string?)e.Values.FirstOrDefault(v => v.Name == "messageId").Value ?? "",
        Payload:      (string?)e.Values.FirstOrDefault(v => v.Name == "payload").Value ?? "",
        Error:        (string?)e.Values.FirstOrDefault(v => v.Name == "error").Value ?? "",
        FailedAt:     (string?)e.Values.FirstOrDefault(v => v.Name == "failedAt").Value ?? ""
    );
}
