using FlowForge.Domain.Enums;
using FlowForge.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace FlowForge.JobAutomator.Evaluators;

public class TriggerConditionEvaluator(ILogger<TriggerConditionEvaluator> logger)
{
    public bool Evaluate(
        TriggerConditionNode node,
        IReadOnlyDictionary<string, bool> triggerResults)
    {
        if (node.TriggerName is not null)
        {
            if (!triggerResults.TryGetValue(node.TriggerName, out var result))
            {
                logger.LogWarning(
                    "Condition references TriggerName '{TriggerName}' with no evaluation result — treating as false",
                    node.TriggerName);
                return false;
            }
            return result;
        }

        var childResults = node.Nodes!.Select(n => Evaluate(n, triggerResults)).ToList();

        return node.Operator switch
        {
            ConditionOperator.And => childResults.All(r => r),
            ConditionOperator.Or => childResults.Any(r => r),
            _ => throw new ArgumentOutOfRangeException(nameof(node.Operator))
        };
    }
}
