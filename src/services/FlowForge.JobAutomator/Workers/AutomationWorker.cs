using FlowForge.Contracts.Events;
using FlowForge.Domain.Enums;
using FlowForge.Domain.Repositories;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.JobAutomator.Evaluators;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowForge.JobAutomator.Workers;

public class AutomationWorker(
    IServiceScopeFactory scopeFactory,
    ITriggerEvaluator[] evaluators,
    TriggerConditionEvaluator conditionEvaluator,
    IMessagePublisher publisher,
    ILogger<AutomationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("AutomationWorker starting...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IAutomationRepository>();
                var automations = await repository.GetAllAsync(stoppingToken);

                foreach (var automation in automations)
                {
                    var triggerResults = new Dictionary<Guid, bool>();
                    
                    foreach (var trigger in automation.Triggers)
                    {
                        var evaluator = evaluators.FirstOrDefault(e => Matches(e, trigger.Type));
                        if (evaluator != null)
                        {
                            var result = await evaluator.EvaluateAsync(trigger, stoppingToken);
                            triggerResults[trigger.Id] = result;
                        }
                    }

                    bool isTriggered = false;
                    if (automation.TriggerCondition != null)
                    {
                        isTriggered = conditionEvaluator.Evaluate(automation.TriggerCondition, triggerResults);
                    }
                    else
                    {
                        // Default behavior: any trigger fires it if no condition
                        isTriggered = triggerResults.Values.Any(r => r);
                    }

                    if (isTriggered)
                    {
                        logger.LogInformation("Automation {Name} ({Id}) triggered!", automation.Name, automation.Id);
                        var @event = new AutomationTriggeredEvent(automation.Id, automation.HostGroupId, DateTimeOffset.UtcNow);
                        await publisher.PublishAsync(@event, null, stoppingToken);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error occurred while evaluating automations.");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private bool Matches(ITriggerEvaluator evaluator, TriggerType type)
    {
        return type switch
        {
            TriggerType.Schedule => evaluator is ScheduleTriggerEvaluator,
            TriggerType.Sql => evaluator is SqlTriggerEvaluator,
            TriggerType.JobCompleted => evaluator is JobCompletedTriggerEvaluator,
            TriggerType.Webhook => evaluator is WebhookTriggerEvaluator,
            _ => false
        };
    }
}
