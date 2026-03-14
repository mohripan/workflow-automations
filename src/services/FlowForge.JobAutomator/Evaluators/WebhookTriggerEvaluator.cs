using FlowForge.Domain.Entities;
using FlowForge.Infrastructure.Caching;

namespace FlowForge.JobAutomator.Evaluators;

public class WebhookTriggerEvaluator(IRedisService redis) : ITriggerEvaluator
{
    public async Task<bool> EvaluateAsync(Trigger trigger, CancellationToken ct)
    {
        var firedKey = $"trigger:webhook:{trigger.Id}:fired";
        var fired = await redis.GetAsync(firedKey);
        
        if (fired != null)
        {
            // Reset the flag
            await redis.DeleteAsync(firedKey);
            return true;
        }

        return false;
    }
}
