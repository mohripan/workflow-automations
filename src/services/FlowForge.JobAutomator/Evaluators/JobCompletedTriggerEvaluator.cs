using FlowForge.Domain.Entities;
using FlowForge.Domain.Enums;
using FlowForge.Domain.Repositories;
using FlowForge.Infrastructure.Messaging.Redis;
using FlowForge.Infrastructure.Caching;
using System.Text.Json;

namespace FlowForge.JobAutomator.Evaluators;

public class JobCompletedTriggerEvaluator(IRedisService redis) : ITriggerEvaluator
{
    public async Task<bool> EvaluateAsync(Trigger trigger, CancellationToken ct)
    {
        var config = JsonSerializer.Deserialize<JobCompletedConfig>(trigger.ConfigJson);
        if (config == null || config.AutomationId == Guid.Empty) return false;

        // Check redis if this automation has a recently completed job
        var completedKey = $"automation:{config.AutomationId}:last_job_completed";
        var jobStatus = await redis.GetAsync(completedKey);
        
        if (jobStatus == null) return false;

        // Reset the flag so it doesn't trigger again
        await redis.DeleteAsync(completedKey);
        return true;
    }

    private record JobCompletedConfig(Guid AutomationId);
}
