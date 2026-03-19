namespace FlowForge.Domain.Entities;

public class OutboxMessage : BaseEntity<Guid>
{
    public string EventType { get; private set; } = default!;
    public string Payload { get; private set; } = default!;
    public string StreamName { get; private set; } = default!;
    public DateTimeOffset? SentAt { get; private set; }

    private OutboxMessage() { }

    public static OutboxMessage Create(string eventType, string payload, string streamName) => new()
    {
        Id = Guid.NewGuid(),
        EventType = eventType,
        Payload = payload,
        StreamName = streamName
    };

    public void MarkSent() => SentAt = DateTimeOffset.UtcNow;
}
