namespace FlowForge.Infrastructure.Messaging.Abstractions;

/// <summary>
/// Handles a single event of type <typeparamref name="TEvent"/>.
/// Implementations contain the pure business logic for processing an event,
/// decoupled from the transport mechanism (Redis pull, Dapr push, etc.).
/// </summary>
public interface IEventHandler<in TEvent> where TEvent : class
{
    Task HandleAsync(TEvent @event, CancellationToken ct);
}
