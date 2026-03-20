namespace FlowForge.JobOrchestrator.Options;

public class HeartbeatMonitorOptions
{
    public const string SectionName = "HeartbeatMonitor";

    public int CheckIntervalSeconds { get; init; } = 15;
}
