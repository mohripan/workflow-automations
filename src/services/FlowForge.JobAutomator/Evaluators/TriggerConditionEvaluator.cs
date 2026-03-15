using FlowForge.Contracts.Events;

namespace FlowForge.JobAutomator.Evaluators;

public class TriggerConditionEvaluator
{
    public bool Evaluate(TriggerConditionSnapshot node, IReadOnlyDictionary<Guid, bool> triggerResults)
    {
        if (node.TriggerId.HasValue)
        {
            return triggerResults.GetValueOrDefault(node.TriggerId.Value, false);
        }

        if (node.Nodes == null || node.Nodes.Count == 0) return true;

        var childResults = node.Nodes.Select(n => Evaluate(n, triggerResults)).ToList();

        return node.Operator switch
        {
            FlowForge.Contracts.Events.ConditionOperator.And => childResults.All(r => r),
            FlowForge.Contracts.Events.ConditionOperator.Or => childResults.Any(r => r),
            _ => false
        };
    }
}
