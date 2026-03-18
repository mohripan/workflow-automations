using FlowForge.Contracts.Events;
using FlowForge.Domain.Entities;
using FlowForge.Domain.Exceptions;
using FlowForge.Domain.Repositories;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.Redis;

namespace FlowForge.WebApi.Workers;

public class AutomationTriggeredConsumer(
    IMessageConsumer consumer,
    IMessagePublisher publisher,
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

                var automation = await automationRepo.GetByIdAsync(@event.AutomationId, stoppingToken)
                    ?? throw new AutomationNotFoundException(@event.AutomationId);

                var hostGroup = await hostGroupRepo.GetByIdAsync(automation.HostGroupId, stoppingToken)
                    ?? throw new InvalidAutomationException($"Host group {automation.HostGroupId} not found");

                var jobRepo = scope.ServiceProvider.GetRequiredKeyedService<IJobRepository>(hostGroup.ConnectionId);

                var job = Job.Create(
                    automationId: automation.Id,
                    taskId: automation.TaskId,
                    connectionId: hostGroup.ConnectionId,
                    hostGroupId: automation.HostGroupId,
                    triggeredAt: @event.TriggeredAt);

                await jobRepo.SaveAsync(job, stoppingToken);

                await publisher.PublishAsync(new JobCreatedEvent(
                    JobId: job.Id,
                    ConnectionId: hostGroup.ConnectionId,
                    AutomationId: job.AutomationId,
                    HostGroupId: job.HostGroupId,
                    CreatedAt: job.CreatedAt
                ), ct: stoppingToken);

                logger.LogInformation("Job {JobId} created for automation {AutomationId}", job.Id, automation.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process AutomationTriggeredEvent for automation {AutomationId}", @event.AutomationId);
            }
        }
    }
}
