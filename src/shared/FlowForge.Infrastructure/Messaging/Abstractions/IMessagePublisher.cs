namespace FlowForge.Infrastructure.Messaging.Abstractions;

public interface IMessagePublisher
{
    Task PublishAsync<TEvent>(TEvent @event, string? targetStream = null, CancellationToken ct = default)
        where TEvent : class;
}
