using FlowForge.Contracts.Events;
using FlowForge.Domain.Entities;
using FlowForge.Domain.Enums;
using FlowForge.Domain.Exceptions;
using FlowForge.Domain.Repositories;
using FlowForge.Infrastructure.Messaging;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.DeadLetter;
using FlowForge.Infrastructure.Messaging.Outbox;
using FlowForge.Infrastructure.Telemetry;
using System.Text.Json;

namespace FlowForge.WebApi.Handlers;

public class AutomationTriggeredHandler(
    IServiceScopeFactory scopeFactory,
    IDlqWriter dlqWriter,
    ILogger<AutomationTriggeredHandler> logger) : IEventHandler<AutomationTriggeredEvent>
{
    public async Task HandleAsync(AutomationTriggeredEvent @event, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var automationRepo = scope.ServiceProvider.GetRequiredService<IAutomationRepository>();
            var hostGroupRepo = scope.ServiceProvider.GetRequiredService<IHostGroupRepository>();
            var outboxWriter = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();

            var automation = await automationRepo.GetByIdAsync(@event.AutomationId, ct)
                ?? throw new AutomationNotFoundException(@event.AutomationId);

            if (!automation.IsEnabled)
            {
                logger.LogInformation(
                    "Automation {AutomationId} is disabled — dropping triggered event.", @event.AutomationId);
                return;
            }

            var hostGroup = await hostGroupRepo.GetByIdAsync(automation.HostGroupId, ct)
                ?? throw new InvalidAutomationException($"Host group {automation.HostGroupId} not found");

            var jobRepo = scope.ServiceProvider.GetRequiredKeyedService<IJobRepository>(hostGroup.ConnectionId);

            // Duplicate job prevention
            if (automation.ActiveJobId is Guid activeJobId)
            {
                var activeJob = await jobRepo.GetByIdAsync(activeJobId, ct);
                if (activeJob is not null && !activeJob.Status.IsTerminal())
                {
                    logger.LogWarning(
                        "Automation {AutomationId} already has active job {ActiveJobId} in status {Status}. Skipping duplicate.",
                        automation.Id, activeJobId, activeJob.Status);
                    return;
                }

                automation.ClearActiveJob();
            }

            var job = Job.Create(
                automationId: automation.Id,
                taskId: automation.TaskId,
                connectionId: hostGroup.ConnectionId,
                hostGroupId: automation.HostGroupId,
                triggeredAt: @event.TriggeredAt,
                timeoutSeconds: @event.TimeoutSeconds,
                retryAttempt: @event.RetryAttempt,
                maxRetries: @event.MaxRetries,
                taskConfig: @event.TaskConfig);

            automation.SetActiveJob(job.Id);
            await outboxWriter.WriteAsync(new JobCreatedEvent(
                JobId: job.Id,
                ConnectionId: hostGroup.ConnectionId,
                AutomationId: job.AutomationId,
                HostGroupId: job.HostGroupId,
                CreatedAt: job.CreatedAt,
                TimeoutSeconds: job.TimeoutSeconds
            ), ct);
            await automationRepo.SaveAsync(automation, ct);
            await jobRepo.SaveAsync(job, ct);

            FlowForgeMetrics.JobsCreated.Add(1,
                new KeyValuePair<string, object?>("automation_id", automation.Id),
                new KeyValuePair<string, object?>("host_group_id", automation.HostGroupId));

            logger.LogInformation("Job {JobId} created for automation {AutomationId}", job.Id, automation.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process AutomationTriggeredEvent for automation {AutomationId}. Sending to DLQ.", @event.AutomationId);
            await dlqWriter.WriteAsync(
                sourceStream: TopicNames.AutomationTriggered,
                messageId: @event.AutomationId.ToString(),
                payload: JsonSerializer.Serialize(@event),
                error: ex.Message);
        }
    }
}
