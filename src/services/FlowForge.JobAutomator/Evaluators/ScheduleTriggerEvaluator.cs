using FlowForge.Domain.Entities;
using FlowForge.Infrastructure.Caching;
using System.Text.Json;

namespace FlowForge.JobAutomator.Evaluators;

public class ScheduleTriggerEvaluator(IRedisService redis) : ITriggerEvaluator
{
    public async Task<bool> EvaluateAsync(Trigger trigger, CancellationToken ct)
    {
        // For a schedule, we might check if the current time matches the cron
        // In a real system, we might use a scheduler to trigger these.
        // For this task, we'll check if a redis key exists (set by some scheduler)
        // Or we check the cron expression.
        
        var config = JsonSerializer.Deserialize<ScheduleConfig>(trigger.ConfigJson);
        if (config == null) return false;

        // Simple mock: check redis for the last execution time
        var lastExecutionKey = $"trigger:schedule:{trigger.Id}:last";
        var lastExecutionStr = await redis.GetAsync(lastExecutionKey);
        
        if (DateTimeOffset.TryParse(lastExecutionStr, out var lastExecution))
        {
            // If it was executed in the last minute, don't trigger again (simple mock)
            if (DateTimeOffset.UtcNow - lastExecution < TimeSpan.FromMinutes(1))
                return false;
        }

        // In a real system we would use Cronos or similar to check if it's time
        // For this implementation, we'll just assume it's time if it hasn't run in a while
        await redis.SetAsync(lastExecutionKey, DateTimeOffset.UtcNow.ToString(), TimeSpan.FromDays(1));
        return true;
    }

    private record ScheduleConfig(string Cron);
}
