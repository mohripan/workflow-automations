using FlowForge.Infrastructure.Messaging.Abstractions;
using Microsoft.Extensions.Logging;

namespace FlowForge.Infrastructure.Messaging.Dapr;

/// <summary>
/// No-op infrastructure setup for Dapr. Topic creation and consumer group
/// management is handled by the Dapr runtime and Kafka automatically.
/// </summary>
public class DaprMessagingInfrastructure(
    ILogger<DaprMessagingInfrastructure> logger) : IMessagingInfrastructure
{
    public Task SetupAsync(IReadOnlyList<TopicSubscription> subscriptions, CancellationToken ct = default)
    {
        logger.LogInformation("Dapr messaging infrastructure ready — {Count} subscriptions managed by Dapr runtime",
            subscriptions.Count);
        return Task.CompletedTask;
    }
}
