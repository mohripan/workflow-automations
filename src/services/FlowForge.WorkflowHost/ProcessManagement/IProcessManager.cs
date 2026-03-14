namespace FlowForge.WorkflowHost.ProcessManagement;

public interface IProcessManager
{
    Task RunAsync(Guid jobId, string connectionId, CancellationToken ct);
}
