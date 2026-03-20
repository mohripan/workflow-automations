using FlowForge.Contracts.Events;
using FlowForge.Domain.Entities;
using FlowForge.Domain.Enums;
using FlowForge.Domain.Exceptions;
using FlowForge.Domain.Repositories;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.Outbox;
using FlowForge.Infrastructure.Messaging.Redis;
using FlowForge.Infrastructure.Telemetry;

namespace FlowForge.WebApi.Workers;

public class AutomationTriggeredConsumer(
    IMessageConsumer consumer,
    IServiceScopeFactory scopeFactory,
    ILogger<AutomationTriggeredConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var @event in consumer.ConsumeAsync<AutomationTriggeredEvent>(
            StreamNames.AutomationTriggered, "webapi", "webapi-1", stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var automationRepo = scope.ServiceProvider.GetRequiredService<IAutomationRepository>();
                var hostGroupRepo = scope.ServiceProvider.GetRequiredService<IHostGroupRepository>();
                var outboxWriter = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();

                var automation = await automationRepo.GetByIdAsync(@event.AutomationId, stoppingToken)
                    ?? throw new AutomationNotFoundException(@event.AutomationId);

                var hostGroup = await hostGroupRepo.GetByIdAsync(automation.HostGroupId, stoppingToken)
                    ?? throw new InvalidAutomationException($"Host group {automation.HostGroupId} not found");

                var jobRepo = scope.ServiceProvider.GetRequiredKeyedService<IJobRepository>(hostGroup.ConnectionId);

                // Duplicate job prevention
                if (automation.ActiveJobId is Guid activeJobId)
                {
                    var activeJob = await jobRepo.GetByIdAsync(activeJobId, stoppingToken);
                    if (activeJob is not null && !activeJob.Status.IsTerminal())
                    {
                        logger.LogWarning(
                            "Automation {AutomationId} already has active job {ActiveJobId} in status {Status}. Skipping duplicate.",
                            automation.Id, activeJobId, activeJob.Status);
                        continue;
                    }

                    // Job is terminal or missing — clear stale reference before proceeding
                    automation.ClearActiveJob();
                }

                var job = Job.Create(
                    automationId: automation.Id,
                    taskId: automation.TaskId,
                    connectionId: hostGroup.ConnectionId,
                    hostGroupId: automation.HostGroupId,
                    triggeredAt: @event.TriggeredAt);

                automation.SetActiveJob(job.Id);
                await outboxWriter.WriteAsync(new JobCreatedEvent(
                    JobId: job.Id,
                    ConnectionId: hostGroup.ConnectionId,
                    AutomationId: job.AutomationId,
                    HostGroupId: job.HostGroupId,
                    CreatedAt: job.CreatedAt
                ), stoppingToken);
                await automationRepo.SaveAsync(automation, stoppingToken);  // commits automation + outbox message
                await jobRepo.SaveAsync(job, stoppingToken);

                FlowForgeMetrics.JobsCreated.Add(1,
                    new KeyValuePair<string, object?>("automation_id", automation.Id),
                    new KeyValuePair<string, object?>("host_group_id", automation.HostGroupId));

                logger.LogInformation("Job {JobId} created for automation {AutomationId}", job.Id, automation.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process AutomationTriggeredEvent for automation {AutomationId}", @event.AutomationId);
            }
        }
    }
}
