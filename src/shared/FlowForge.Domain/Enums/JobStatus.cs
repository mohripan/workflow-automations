namespace FlowForge.Domain.Enums;

public enum JobStatus
{
    Pending,
    Started,
    InProgress,
    Completed,
    CompletedUnsuccessfully,
    Error,
    Cancel,
    Cancelled,
    Removed
}

public static class JobStatusExtensions
{
    public static bool IsCancellable(this JobStatus status)
        => status is JobStatus.Pending or JobStatus.Started or JobStatus.InProgress;

    public static bool IsTerminal(this JobStatus status)
        => status is JobStatus.Completed or JobStatus.CompletedUnsuccessfully
                  or JobStatus.Error or JobStatus.Cancelled or JobStatus.Removed;
}
