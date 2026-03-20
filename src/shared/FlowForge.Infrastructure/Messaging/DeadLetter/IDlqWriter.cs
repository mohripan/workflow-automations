namespace FlowForge.Infrastructure.Messaging.DeadLetter;

public interface IDlqWriter
{
    Task WriteAsync(string sourceStream, string messageId, string payload, string error, CancellationToken ct = default);
}
