namespace FlowForge.JobAutomator.Options;

public class AutomationWorkerOptions
{
    public const string SectionName = "AutomationWorker";

    public int EvaluationIntervalSeconds { get; init; } = 5;
}
