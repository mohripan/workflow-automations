using FlowForge.Domain.Entities;
using FlowForge.Domain.Enums;
using FlowForge.Domain.ValueObjects;

namespace FlowForge.JobAutomator.Evaluators;

public class TriggerConditionEvaluator
{
    public bool Evaluate(TriggerCondition condition, Dictionary<Guid, bool> triggerResults)
    {
        if (condition.Nodes.Count == 0) return true;

        var nodeResults = condition.Nodes.Select(node =>
        {
            if (node.TriggerId.HasValue)
            {
                return triggerResults.GetValueOrDefault(node.TriggerId.Value, false);
            }
            else if (node.SubCondition != null)
            {
                return Evaluate(node.SubCondition, triggerResults);
            }
            return false;
        }).ToList();

        return condition.Operator switch
        {
            ConditionOperator.And => nodeResults.All(r => r),
            ConditionOperator.Or => nodeResults.Any(r => r),
            _ => false
        };
    }
}
