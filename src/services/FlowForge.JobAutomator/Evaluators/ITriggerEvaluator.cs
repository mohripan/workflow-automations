using FlowForge.Domain.Entities;

namespace FlowForge.JobAutomator.Evaluators;

public interface ITriggerEvaluator
{
    Task<bool> EvaluateAsync(Trigger trigger, CancellationToken ct);
}
