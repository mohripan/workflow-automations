namespace FlowForge.Infrastructure.Messaging.Dapr;

/// <summary>
/// Configuration for the Dapr messaging provider.
/// </summary>
public class DaprOptions
{
    public const string SectionName = "Messaging:Dapr";

    /// <summary>
    /// The Dapr pub/sub component name. Defaults to "flowforge-pubsub".
    /// </summary>
    public string PubSubName { get; init; } = "flowforge-pubsub";
}
