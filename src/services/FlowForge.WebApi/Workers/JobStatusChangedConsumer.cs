using FlowForge.Contracts.Events;
using FlowForge.Domain.Enums;
using FlowForge.Domain.Repositories;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.Redis;
using FlowForge.Infrastructure.Telemetry;
using FlowForge.WebApi.DTOs.Responses;
using FlowForge.WebApi.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace FlowForge.WebApi.Workers;
public class JobStatusChangedConsumer(
    IMessageConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IHubContext<JobStatusHub, IJobStatusClient> hubContext,
    ILogger<JobStatusChangedConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var @event in consumer.ConsumeAsync<JobStatusChangedEvent>(
            StreamNames.JobStatusChanged, "webapi", "webapi-1", stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var jobRepo = scope.ServiceProvider.GetRequiredKeyedService<IJobRepository>(@event.ConnectionId);

                var job = await jobRepo.GetByIdAsync(@event.JobId, stoppingToken);

                if (job is null)
                {
                    logger.LogWarning("Received status change for unknown job {JobId}", @event.JobId);
                    continue;
                }

                job.Transition(@event.Status);
                if (@event.Message is not null) job.SetMessage(@event.Message);

                await jobRepo.SaveAsync(job, stoppingToken);

                if (@event.Status == JobStatus.Completed)
                    FlowForgeMetrics.JobsCompleted.Add(1,
                        new KeyValuePair<string, object?>("host_group_id", job.HostGroupId));
                else if (@event.Status is JobStatus.Error or JobStatus.CompletedUnsuccessfully)
                    FlowForgeMetrics.JobsFailed.Add(1,
                        new KeyValuePair<string, object?>("host_group_id", job.HostGroupId));

                // Clear ActiveJobId on the automation when job reaches a terminal status
                if (@event.Status.IsTerminal())
                {
                    var automationRepo = scope.ServiceProvider.GetRequiredService<IAutomationRepository>();
                    var automation = await automationRepo.GetByIdAsync(@event.AutomationId, stoppingToken);
                    if (automation is not null)
                    {
                        automation.ClearActiveJob();
                        await automationRepo.SaveAsync(automation, stoppingToken);
                    }
                }

                await hubContext.Clients
                    .Group($"job:{job.Id}")
                    .OnJobStatusChanged(new JobStatusUpdate(
                        JobId: job.Id,
                        Status: job.Status,
                        Message: job.Message,
                        UpdatedAt: @event.UpdatedAt));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process JobStatusChangedEvent for job {JobId}", @event.JobId);
            }
        }
    }
}
