namespace FlowForge.WorkflowEngine.Handlers;

public interface IWorkflowHandler
{
    string TaskId { get; }
    Task<WorkflowResult> ExecuteAsync(WorkflowContext context, CancellationToken ct);
}

public class WorkflowHandlerRegistry(IEnumerable<IWorkflowHandler> handlers)
{
    private readonly IReadOnlyDictionary<string, IWorkflowHandler> _handlers = 
        handlers.ToDictionary(h => h.TaskId, StringComparer.OrdinalIgnoreCase);

    public IWorkflowHandler Get(string taskId) => 
        _handlers.TryGetValue(taskId, out var handler) 
            ? handler 
            : throw new KeyNotFoundException($"Unknown TaskId: {taskId}");
}
