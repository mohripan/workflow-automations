using FlowForge.Contracts.Events;
using FlowForge.Domain.Enums;
using FlowForge.Domain.Repositories;
using FlowForge.Infrastructure.Messaging;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.DeadLetter;
using FlowForge.Infrastructure.Messaging.Outbox;
using FlowForge.Infrastructure.Telemetry;
using FlowForge.WebApi.DTOs.Responses;
using FlowForge.WebApi.Hubs;
using Microsoft.AspNetCore.SignalR;
using System.Text.Json;

namespace FlowForge.WebApi.Handlers;

public class JobStatusChangedHandler(
    IServiceScopeFactory scopeFactory,
    IHubContext<JobStatusHub, IJobStatusClient> hubContext,
    IDlqWriter dlqWriter,
    ILogger<JobStatusChangedHandler> logger) : IEventHandler<JobStatusChangedEvent>
{
    public async Task HandleAsync(JobStatusChangedEvent @event, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var jobRepo = scope.ServiceProvider.GetRequiredKeyedService<IJobRepository>(@event.ConnectionId);

            var job = await jobRepo.GetByIdAsync(@event.JobId, ct);

            if (job is null)
            {
                logger.LogWarning("Received status change for unknown job {JobId}", @event.JobId);
                return;
            }

            job.Transition(@event.Status);
            if (@event.Message is not null) job.SetMessage(@event.Message);
            if (@event.OutputJson is not null) job.SetOutput(@event.OutputJson);

            await jobRepo.SaveAsync(job, ct);

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
                var automation = await automationRepo.GetByIdAsync(@event.AutomationId, ct);
                if (automation is not null)
                {
                    automation.ClearActiveJob();

                    // Schedule retry if the job failed and retries remain
                    if (@event.Status is JobStatus.Error or JobStatus.CompletedUnsuccessfully
                        && job.RetryAttempt < job.MaxRetries)
                    {
                        var outboxWriter = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();
                        await outboxWriter.WriteAsync(new AutomationTriggeredEvent(
                            AutomationId: automation.Id,
                            HostGroupId:  automation.HostGroupId,
                            ConnectionId: @event.ConnectionId,
                            TaskId:       job.TaskId,
                            TriggeredAt:  DateTimeOffset.UtcNow,
                            TimeoutSeconds: job.TimeoutSeconds,
                            MaxRetries:   job.MaxRetries,
                            RetryAttempt: job.RetryAttempt + 1,
                            TaskConfig:   job.TaskConfig), ct);

                        logger.LogInformation(
                            "Job {JobId} failed (attempt {Attempt}/{Max}); scheduling retry.",
                            job.Id, job.RetryAttempt + 1, job.MaxRetries);
                    }

                    await automationRepo.SaveAsync(automation, ct);
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
            logger.LogError(ex, "Failed to process JobStatusChangedEvent for job {JobId}. Sending to DLQ.", @event.JobId);
            await dlqWriter.WriteAsync(
                sourceStream: TopicNames.JobStatusChanged,
                messageId: @event.JobId.ToString(),
                payload: JsonSerializer.Serialize(@event),
                error: ex.Message);
        }
    }
}
