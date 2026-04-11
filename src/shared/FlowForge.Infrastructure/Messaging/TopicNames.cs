namespace FlowForge.Infrastructure.Messaging;

/// <summary>
/// Provider-agnostic topic/destination names for inter-service messaging.
/// Maps event types to logical topic names that each provider translates
/// to its native destination (Redis stream name, Kafka topic, Dapr topic, etc.).
/// </summary>
public static class TopicNames
{
    public const string AutomationTriggered = "automation-triggered";
    public const string AutomationChanged = "automation-changed";
    public const string JobCreated = "job-created";
    public const string JobAssigned = "job-assigned";
    public const string JobStatusChanged = "job-status-changed";
    public const string JobCancelRequested = "job-cancel-requested";
    public const string Dlq = "dlq";

    public static string HostTopic(string hostId) => $"host-{hostId}";

    public static string ForEventType(string typeName) => typeName switch
    {
        "AutomationTriggeredEvent" => AutomationTriggered,
        "AutomationChangedEvent"   => AutomationChanged,
        "JobCreatedEvent"          => JobCreated,
        "JobAssignedEvent"         => JobAssigned,
        "JobStatusChangedEvent"    => JobStatusChanged,
        "JobCancelRequestedEvent"  => JobCancelRequested,
        _ => throw new ArgumentException($"Unknown event type: {typeName}")
    };
}
