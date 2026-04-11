namespace FlowForge.Infrastructure.Messaging;

/// <summary>
/// Configuration for the messaging provider selection.
/// </summary>
public class MessagingOptions
{
    public const string SectionName = "Messaging";

    /// <summary>
    /// The messaging provider to use. Supported values: "redis", "dapr".
    /// </summary>
    public string Provider { get; init; } = "redis";
}
