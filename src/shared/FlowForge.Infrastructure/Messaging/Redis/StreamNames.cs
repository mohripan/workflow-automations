namespace FlowForge.Infrastructure.Messaging.Redis;

public static class StreamNames
{
    public const string AutomationTriggered = "flowforge:automation-triggered";
    public const string AutomationChanged = "flowforge:automation-changed";
    public const string JobCreated = "flowforge:job-created";
    public const string JobAssigned = "flowforge:job-assigned";
    public const string JobStatusChanged = "flowforge:job-status-changed";
    public const string JobCancelRequested = "flowforge:job-cancel-requested";

    public static string HostStream(string hostId) => $"flowforge:host:{hostId}";

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
