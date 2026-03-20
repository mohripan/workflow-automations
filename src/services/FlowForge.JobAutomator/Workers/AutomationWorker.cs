using System.Diagnostics;
using FlowForge.Contracts.Events;
using FlowForge.Infrastructure.Caching;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Telemetry;
using FlowForge.JobAutomator.Cache;
using FlowForge.JobAutomator.Evaluators;
using FlowForge.JobAutomator.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowForge.JobAutomator.Workers;

public class AutomationWorker(
    AutomationCache cache,
    IEnumerable<ITriggerEvaluator> evaluators,
    TriggerConditionEvaluator conditionEvaluator,
    IMessagePublisher publisher,
    IRedisService redis,
    IOptions<AutomationWorkerOptions> options,
    ILogger<AutomationWorker> logger) : BackgroundService
{
    private readonly AutomationWorkerOptions _options = options.Value;
    private static readonly ActivitySource _activitySource = new("FlowForge.JobAutomator");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("AutomationWorker starting...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EvaluateAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "AutomationWorker evaluation pass failed; retrying after delay");
                await Task.Delay(TimeSpan.FromSeconds(_options.EvaluationIntervalSeconds), stoppingToken);
                continue;
            }

            await redis.SetAsync("automator:last-evaluated", DateTimeOffset.UtcNow.ToString("O"));
            await Task.Delay(TimeSpan.FromSeconds(_options.EvaluationIntervalSeconds), stoppingToken);
        }

        logger.LogInformation("AutomationWorker stopped");
    }

    private async Task EvaluateAllAsync(CancellationToken ct)
    {
        var all = cache.GetAll();
        var enabled = all.Where(a => a.IsEnabled).ToList();

        logger.LogDebug(
            "Evaluation pass: {EnabledCount} enabled, {DisabledCount} disabled",
            enabled.Count, all.Count - enabled.Count);

        foreach (var automation in enabled)
            await EvaluateAutomationAsync(automation, ct);
    }

    private async Task EvaluateAutomationAsync(AutomationSnapshot automation, CancellationToken ct)
    {
        using var activity = _activitySource.StartActivity($"evaluate automation {automation.Id}");

        if (automation.ConditionRoot is null)
        {
            logger.LogError(
                "Automation {AutomationId} ({Name}) has null ConditionRoot — skipping.",
                automation.Id, automation.Name);
            return;
        }

        logger.LogDebug("Evaluating automation {AutomationId} ({Name})", automation.Id, automation.Name);

        // Dictionary keyed by TriggerName (string) — matches condition tree references
        var results = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var trigger in automation.Triggers)
        {
            var evaluator = evaluators.SingleOrDefault(e => e.TypeId == trigger.TypeId);
            if (evaluator is null)
            {
                logger.LogWarning(
                    "No evaluator registered for TypeId '{TypeId}' " +
                    "(trigger '{TriggerName}' on automation {AutomationId}). Treating as false.",
                    trigger.TypeId, trigger.Name, automation.Id);
                results[trigger.Name] = false;
                continue;
            }

            results[trigger.Name] = await evaluator.EvaluateAsync(trigger, ct);

            logger.LogDebug(
                "Trigger '{TriggerName}' (type={TypeId}) on {AutomationId}: fired={Fired}",
                trigger.Name, trigger.TypeId, automation.Id, results[trigger.Name]);
        }

        var conditionMet = conditionEvaluator.Evaluate(automation.ConditionRoot, results);

        logger.LogDebug(
            "Automation {AutomationId} ({Name}) condition result: {ConditionMet}",
            automation.Id, automation.Name, conditionMet);

        if (!conditionMet) return;

        await publisher.PublishAsync(new AutomationTriggeredEvent(
            AutomationId: automation.Id,
            HostGroupId: automation.HostGroupId,
            ConnectionId: automation.ConnectionId,
            TaskId: automation.TaskId,
            TriggeredAt: DateTimeOffset.UtcNow,
            TimeoutSeconds: automation.TimeoutSeconds,
            MaxRetries: automation.MaxRetries), ct: ct);

        FlowForgeMetrics.TriggersFired.Add(1,
            new KeyValuePair<string, object?>("automation_id", automation.Id));

        logger.LogInformation(
            "Automation {AutomationId} ({Name}) triggered — event published",
            automation.Id, automation.Name);
    }
}
