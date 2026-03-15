using FlowForge.Contracts.Events;
using FlowForge.Domain.Enums;

namespace FlowForge.JobAutomator.Evaluators;

public interface ITriggerEvaluator
{
    TriggerType Type { get; }
    Task<bool> EvaluateAsync(TriggerSnapshot trigger, CancellationToken ct);
}
