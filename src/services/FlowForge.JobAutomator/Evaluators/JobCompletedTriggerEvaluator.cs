using FlowForge.Contracts.Events;
using FlowForge.Domain.Enums;
using FlowForge.Domain.Triggers;
using FlowForge.Infrastructure.Caching;
using FlowForge.JobAutomator.Cache;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace FlowForge.JobAutomator.Evaluators;

public class JobCompletedTriggerEvaluator(
    IRedisService redis,
    AutomationCache cache,
    ILogger<JobCompletedTriggerEvaluator> logger) : ITriggerEvaluator
{
    public string TypeId => TriggerTypes.JobCompleted;

    public async Task HandleJobStatusChangedAsync(JobStatusChangedEvent @event, CancellationToken ct)
    {
        if (@event.Status != JobStatus.Completed) return;

        var affected = cache.GetAll()
            .SelectMany(a => a.Triggers)
            .Where(t => t.TypeId == TriggerTypes.JobCompleted)
            .Where(t =>
            {
                var cfg = JsonSerializer.Deserialize<JobCompletedTriggerConfig>(t.ConfigJson);
                return cfg?.WatchAutomationId == @event.AutomationId;
            })
            .ToList();

        foreach (var trigger in affected)
        {
            await redis.SetAsync(
                $"trigger:job-completed:{trigger.Id}:fired", "1",
                expiry: TimeSpan.FromMinutes(10));
            logger.LogInformation(
                "JobCompleted trigger '{TriggerName}' flagged — watched automation {WatchedId} completed",
                trigger.Name, @event.AutomationId);
        }
    }

    public async Task<bool> EvaluateAsync(TriggerSnapshot trigger, CancellationToken ct)
    {
        var key = $"trigger:job-completed:{trigger.Id}:fired";
        var fired = await redis.GetAsync(key);
        if (fired is null) return false;
        await redis.DeleteAsync(key);
        logger.LogDebug("JobCompleted trigger '{TriggerName}' consumed fired flag", trigger.Name);
        return true;
    }

    private record JobCompletedTriggerConfig(Guid WatchAutomationId);
}
