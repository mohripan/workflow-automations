using FlowForge.Contracts.Events;
using FlowForge.Domain.Enums;
using FlowForge.Infrastructure.Caching;

namespace FlowForge.JobAutomator.Evaluators;

public class WebhookTriggerEvaluator(IRedisService redis) : ITriggerEvaluator
{
    public TriggerType Type => TriggerType.Webhook;

    public async Task<bool> EvaluateAsync(TriggerSnapshot trigger, CancellationToken ct)
    {
        var firedKey = $"trigger:webhook:{trigger.Id}:fired";
        var fired = await redis.GetAsync(firedKey);
        
        if (fired != null)
        {
            await redis.DeleteAsync(firedKey);
            return true;
        }

        return false;
    }
}
