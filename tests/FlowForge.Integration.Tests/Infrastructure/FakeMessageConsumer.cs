using FlowForge.Infrastructure.Messaging.Abstractions;
using System.Runtime.CompilerServices;

namespace FlowForge.Integration.Tests.Infrastructure;

/// <summary>
/// A test double for <see cref="IMessageConsumer"/> that yields a fixed set of
/// pre-configured events and then completes.  Casting from <c>object</c> means
/// a single instance can be reused for different event types.
/// </summary>
public class FakeMessageConsumer : IMessageConsumer
{
    private readonly IReadOnlyList<object> _events;

    public FakeMessageConsumer(params object[] events) => _events = events;

    public async IAsyncEnumerable<TEvent> ConsumeAsync<TEvent>(
        string streamName,
        string consumerGroup,
        string consumerName,
        [EnumeratorCancellation] CancellationToken ct)
        where TEvent : class
    {
        foreach (var e in _events)
        {
            ct.ThrowIfCancellationRequested();
            if (e is TEvent typed)
                yield return typed;
        }

        // Small yield to allow the caller to process before returning
        await Task.Yield();
    }
}
