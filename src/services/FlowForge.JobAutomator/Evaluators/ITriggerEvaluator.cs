using FlowForge.Contracts.Events;

namespace FlowForge.JobAutomator.Evaluators;

public interface ITriggerEvaluator
{
    string TypeId { get; }
    Task<bool> EvaluateAsync(TriggerSnapshot trigger, CancellationToken ct);
}
