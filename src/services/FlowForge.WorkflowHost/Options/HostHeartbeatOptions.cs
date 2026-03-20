namespace FlowForge.WorkflowHost.Options;

public class HostHeartbeatOptions
{
    public const string SectionName = "HostHeartbeat";

    public int PublishIntervalSeconds { get; init; } = 10;
}
