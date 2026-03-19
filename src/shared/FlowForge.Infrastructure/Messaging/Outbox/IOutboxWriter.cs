namespace FlowForge.Infrastructure.Messaging.Outbox;

public interface IOutboxWriter
{
    Task WriteAsync<TEvent>(TEvent @event, CancellationToken ct = default) where TEvent : class;
}
