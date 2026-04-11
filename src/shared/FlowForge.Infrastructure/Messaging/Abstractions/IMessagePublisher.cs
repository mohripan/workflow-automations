namespace FlowForge.Infrastructure.Messaging.Abstractions;

public interface IMessagePublisher
{
    Task PublishAsync<TEvent>(TEvent @event, string? targetTopic = null, CancellationToken ct = default)
        where TEvent : class;

    /// <summary>
    /// Publishes a pre-serialized JSON payload to the specified topic.
    /// Used by the outbox relay to forward already-serialized messages.
    /// </summary>
    Task PublishRawAsync(string topic, string jsonPayload, CancellationToken ct = default);
}
