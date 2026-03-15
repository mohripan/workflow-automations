namespace FlowForge.WorkflowHost.ProcessManagement;

public interface IProcessManager
{
    Task RunAsync(Guid jobId, Guid automationId, string connectionId, CancellationToken ct);
}
