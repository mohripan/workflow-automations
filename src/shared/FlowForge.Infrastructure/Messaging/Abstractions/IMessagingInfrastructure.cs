namespace FlowForge.Infrastructure.Messaging.Abstractions;

/// <summary>
/// Performs provider-specific infrastructure setup (e.g., creating consumer groups,
/// ensuring topics exist). Called once during application startup.
/// </summary>
public interface IMessagingInfrastructure
{
    Task SetupAsync(IReadOnlyList<TopicSubscription> subscriptions, CancellationToken ct = default);
}

/// <summary>
/// Describes a topic subscription that needs to be provisioned during setup.
/// </summary>
public record TopicSubscription(string TopicName, string GroupName);
