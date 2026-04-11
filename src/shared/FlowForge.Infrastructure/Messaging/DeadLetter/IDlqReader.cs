namespace FlowForge.Infrastructure.Messaging.DeadLetter;

public record DlqEntry(
    string Id,
    string SourceStream,
    string MessageId,
    string Payload,
    string Error,
    string FailedAt
);

public interface IDlqReader
{
    Task<IReadOnlyList<DlqEntry>> GetEntriesAsync(int limit = 50, CancellationToken ct = default);
    Task<DlqEntry?> GetEntryAsync(string id, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
    Task<bool> ReplayAsync(string id, CancellationToken ct = default);
}
