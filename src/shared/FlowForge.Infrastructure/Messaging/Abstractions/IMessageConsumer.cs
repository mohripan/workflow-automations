namespace FlowForge.Infrastructure.Messaging.Abstractions;

public interface IMessageConsumer
{
    IAsyncEnumerable<TEvent> ConsumeAsync<TEvent>(
        string streamName,
        string consumerGroup,
        string consumerName,
        CancellationToken ct)
        where TEvent : class;
}
