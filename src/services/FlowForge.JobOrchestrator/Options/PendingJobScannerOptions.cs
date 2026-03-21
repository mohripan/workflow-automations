namespace FlowForge.JobOrchestrator.Options;

public class PendingJobScannerOptions
{
    public const string SectionName = "PendingJobScanner";

    public int ScanIntervalSeconds { get; init; } = 30;
    public int StaleAfterSeconds { get; init; } = 15;
}
