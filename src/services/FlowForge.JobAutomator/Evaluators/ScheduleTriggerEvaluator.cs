using FlowForge.Contracts.Events;
using FlowForge.Domain.Enums;
using FlowForge.Infrastructure.Caching;

namespace FlowForge.JobAutomator.Evaluators;

public class ScheduleTriggerEvaluator(IRedisService redis) : ITriggerEvaluator
{
    public TriggerType Type => TriggerType.Schedule;

    public async Task<bool> EvaluateAsync(TriggerSnapshot trigger, CancellationToken ct)
    {
        var key = $"trigger:schedule:{trigger.Id}:fired";
        var fired = await redis.GetAsync(key);
        if (fired == null) return false;

        await redis.DeleteAsync(key);
        return true;
    }
}
