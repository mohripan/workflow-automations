using System.Text.Json;

namespace FlowForge.WorkflowEngine.Handlers;

public sealed class WorkflowContext
{
    public Guid JobId { get; init; }
    public string TaskId { get; init; } = default!;
    public string ConnectionId { get; init; } = default!;
    public IReadOnlyDictionary<string, JsonElement> Parameters { get; init; } = new Dictionary<string, JsonElement>();
    public Dictionary<string, object> Outputs { get; } = [];

    public T GetParameter<T>(string key)
    {
        if (!Parameters.TryGetValue(key, out var element))
            throw new KeyNotFoundException($"Parameter '{key}' not found for task '{TaskId}'");
        
        return element.Deserialize<T>() ?? throw new InvalidOperationException($"Could not deserialize parameter '{key}'");
    }
}

public enum WorkflowResultStatus { Completed, Failed, Cancelled, Error }

public record WorkflowResult(WorkflowResultStatus Status, string? Message = null)
{
    public static WorkflowResult Success() => new(WorkflowResultStatus.Completed);
    public static WorkflowResult Failure(string reason) => new(WorkflowResultStatus.Failed, reason);
    public static WorkflowResult Cancellation() => new(WorkflowResultStatus.Cancelled);
    public static WorkflowResult Fault(string message) => new(WorkflowResultStatus.Error, message);
}
