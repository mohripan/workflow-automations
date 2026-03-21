using FlowForge.Contracts.Events;
using FlowForge.Domain.Triggers;
using Quartz;
using Quartz.Impl.Matchers;
using System.Text.Json;

namespace FlowForge.JobAutomator.Initialization;

public interface IQuartzScheduleSync
{
    Task SyncAsync(AutomationSnapshot automation, CancellationToken ct);
    Task RemoveAllAsync(Guid automationId, CancellationToken ct);
}

public class QuartzScheduleSync(ISchedulerFactory schedulerFactory) : IQuartzScheduleSync
{
    private static readonly System.Text.Json.JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task SyncAsync(AutomationSnapshot automation, CancellationToken ct)
    {
        var scheduler = await schedulerFactory.GetScheduler(ct);

        await RemoveAllAsync(automation.Id, ct);

        if (!automation.IsEnabled) return;

        foreach (var trigger in automation.Triggers.Where(t => t.TypeId == TriggerTypes.Schedule))
        {
            var config = System.Text.Json.JsonSerializer.Deserialize<ScheduleTriggerConfig>(trigger.ConfigJson, _jsonOptions);
            if (config == null || string.IsNullOrWhiteSpace(config.CronExpression)) continue;

            var jobKey = new JobKey($"trigger-{trigger.Id}", $"automation-{automation.Id}");

            var job = JobBuilder.Create<ScheduledTriggerJob>()
                .WithIdentity(jobKey)
                .UsingJobData("triggerId", trigger.Id.ToString())
                .Build();

            var quartzTrigger = TriggerBuilder.Create()
                .WithIdentity($"trigger-{trigger.Id}", $"automation-{automation.Id}")
                .WithCronSchedule(config.CronExpression)
                .Build();

            await scheduler.ScheduleJob(job, quartzTrigger, ct);
        }
    }

    public async Task RemoveAllAsync(Guid automationId, CancellationToken ct)
    {
        var scheduler = await schedulerFactory.GetScheduler(ct);
        var groupMatcher = GroupMatcher<JobKey>.GroupEquals($"automation-{automationId}");
        var jobKeys = await scheduler.GetJobKeys(groupMatcher, ct);
        await scheduler.DeleteJobs(jobKeys.ToList(), ct);
    }

    private record ScheduleTriggerConfig(string? CronExpression);
}

public class ScheduledTriggerJob(FlowForge.Infrastructure.Caching.IRedisService redis) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var triggerIdStr = context.JobDetail.JobDataMap.GetString("triggerId");
        if (Guid.TryParse(triggerIdStr, out var triggerId))
        {
            await redis.SetAsync($"trigger:schedule:{triggerId}:fired", "1", TimeSpan.FromMinutes(2));
        }
    }
}
