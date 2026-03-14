namespace FlowForge.Infrastructure.Messaging.Redis;

public static class StreamNames
{
    public const string AutomationTriggered = "flowforge:automation-triggered";
    public const string JobCreated = "flowforge:job-created";
    public const string JobAssigned = "flowforge:job-assigned";
    public const string JobStatusChanged = "flowforge:job-status-changed";
    public const string JobCancelRequested = "flowforge:job-cancel-requested";

    public static string HostStream(string hostId) => $"flowforge:host:{hostId}";
}
