using FlowForge.Contracts.Events;
using FlowForge.Domain.Enums;
using FlowForge.Infrastructure.Caching;
using FlowForge.JobAutomator.Cache;
using System.Text.Json;

namespace FlowForge.JobAutomator.Evaluators;

public class JobCompletedTriggerEvaluator(IRedisService redis, AutomationCache cache) : ITriggerEvaluator
{
    public TriggerType Type => TriggerType.JobCompleted;

    public async Task HandleJobStatusChangedAsync(JobStatusChangedEvent @event, CancellationToken ct)
    {
        if (@event.Status != JobStatus.Completed) return;

        // Find affected triggers from the in-memory cache (no DB lookup)
        var affectedTriggers = cache.GetAll()
            .SelectMany(a => a.Triggers)
            .Where(t => t.Type == TriggerType.JobCompleted)
            .Where(t =>
            {
                var cfg = JsonSerializer.Deserialize<JobCompletedTriggerConfig>(t.ConfigJson);
                return cfg?.WatchAutomationId == @event.AutomationId;
            });

        foreach (var trigger in affectedTriggers)
        {
            await redis.SetAsync(
                $"trigger:job-completed:{trigger.Id}:fired", "1",
                expiry: TimeSpan.FromMinutes(10));
        }
    }

    public async Task<bool> EvaluateAsync(TriggerSnapshot trigger, CancellationToken ct)
    {
        var key = $"trigger:job-completed:{trigger.Id}:fired";
        var fired = await redis.GetAsync(key);
        if (fired == null) return false;

        await redis.DeleteAsync(key);
        return true;
    }

    private record JobCompletedTriggerConfig(Guid WatchAutomationId);
}
