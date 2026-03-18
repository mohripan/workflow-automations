using FlowForge.Contracts.Events;
using FlowForge.Domain.Triggers;
using FlowForge.Infrastructure.Caching;
using Microsoft.Extensions.Logging;

namespace FlowForge.JobAutomator.Evaluators;

public class WebhookTriggerEvaluator(IRedisService redis, ILogger<WebhookTriggerEvaluator> logger) : ITriggerEvaluator
{
    public string TypeId => TriggerTypes.Webhook;

    public async Task<bool> EvaluateAsync(TriggerSnapshot trigger, CancellationToken ct)
    {
        var key = $"trigger:webhook:{trigger.Id}:fired";
        var fired = await redis.GetAsync(key);
        if (fired is null) return false;

        await redis.DeleteAsync(key);
        logger.LogDebug("Webhook trigger '{TriggerName}' consumed fired flag", trigger.Name);
        return true;
    }
}
