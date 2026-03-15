using FlowForge.Contracts.Events;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.JobAutomator.Cache;
using FlowForge.JobAutomator.Evaluators;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using FlowForge.Infrastructure.Caching;

namespace FlowForge.JobAutomator.Workers;

public class AutomationWorker(
    AutomationCache cache,
    IEnumerable<ITriggerEvaluator> evaluators,
    TriggerConditionEvaluator conditionEvaluator,
    IMessagePublisher publisher,
    IRedisService redis,
    ILogger<AutomationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("AutomationWorker starting...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var automations = cache.GetAll().Where(a => a.IsActive);

                foreach (var automation in automations)
                {
                    var triggerResults = new Dictionary<Guid, bool>();
                    
                    foreach (var trigger in automation.Triggers)
                    {
                        var evaluator = evaluators.FirstOrDefault(e => e.Type == trigger.Type);
                        if (evaluator != null)
                        {
                            var result = await evaluator.EvaluateAsync(trigger, stoppingToken);
                            triggerResults[trigger.Id] = result;
                        }
                    }

                    bool isTriggered = automation.ConditionRoot != null 
                        ? conditionEvaluator.Evaluate(automation.ConditionRoot, triggerResults)
                        : triggerResults.Values.Any(r => r);

                    if (isTriggered)
                    {
                        logger.LogInformation("Automation {Name} ({Id}) triggered!", automation.Name, automation.Id);
                        var @event = new AutomationTriggeredEvent(
                            AutomationId: automation.Id, 
                            HostGroupId: automation.HostGroupId, 
                            ConnectionId: automation.ConnectionId,
                            TaskId: automation.TaskId,
                            TriggeredAt: DateTimeOffset.UtcNow);
                        
                        await publisher.PublishAsync(@event, null, stoppingToken);
                    }
                }

                await redis.SetAsync("automator:last-evaluated", DateTimeOffset.UtcNow.ToString("O"));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error occurred while evaluating automations.");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
