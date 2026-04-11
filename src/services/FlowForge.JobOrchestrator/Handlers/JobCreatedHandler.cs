using System.Diagnostics;
using FlowForge.Contracts.Events;
using FlowForge.Domain.Enums;
using FlowForge.Domain.Repositories;
using FlowForge.Infrastructure.Messaging;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.DeadLetter;
using FlowForge.JobOrchestrator.LoadBalancing;
using System.Text.Json;

namespace FlowForge.JobOrchestrator.Handlers;

public class JobCreatedHandler(
    IMessagePublisher publisher,
    IServiceProvider serviceProvider,
    ILoadBalancer loadBalancer,
    IDlqWriter dlqWriter,
    ILogger<JobCreatedHandler> logger) : IEventHandler<JobCreatedEvent>
{
    private static readonly ActivitySource _activitySource = new("FlowForge.JobOrchestrator");

    public async Task HandleAsync(JobCreatedEvent @event, CancellationToken ct)
    {
        try
        {
            using var activity = _activitySource.StartActivity($"dispatch job {@event.JobId}");
            using var scope = serviceProvider.CreateScope();
            var jobRepo = scope.ServiceProvider.GetRequiredKeyedService<IJobRepository>(@event.ConnectionId);
            var hostRepo = scope.ServiceProvider.GetRequiredService<IWorkflowHostRepository>();
            var job = await jobRepo.GetByIdAsync(@event.JobId, ct);

            if (job == null)
            {
                logger.LogWarning("Job {JobId} not found in connection {ConnectionId}", @event.JobId, @event.ConnectionId);
                return;
            }

            var hosts = await hostRepo.GetByGroupAsync(job.HostGroupId, ct);
            var onlineHosts = hosts.Where(h => h.IsOnline).ToList();

            if (onlineHosts.Count == 0)
            {
                logger.LogWarning("No online hosts in group {HostGroupId} for job {JobId}", job.HostGroupId, job.Id);
                return;
            }

            var selectedHost = loadBalancer.Select(onlineHosts, job.HostGroupId);

            job.Transition(JobStatus.Started);
            job.AssignToHost(selectedHost.Id);
            await jobRepo.SaveAsync(job, ct);

            await publisher.PublishAsync(new JobAssignedEvent(
                JobId: job.Id,
                ConnectionId: @event.ConnectionId,
                HostId: selectedHost.Id,
                AutomationId: job.AutomationId,
                AssignedAt: DateTimeOffset.UtcNow
            ), TopicNames.HostTopic(selectedHost.Name), ct);

            logger.LogInformation("Job {JobId} assigned to host {HostId}", job.Id, selectedHost.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error dispatching job {JobId}. Sending to DLQ.", @event.JobId);
            await dlqWriter.WriteAsync(
                sourceStream: TopicNames.JobCreated,
                messageId: @event.JobId.ToString(),
                payload: JsonSerializer.Serialize(@event),
                error: ex.Message);
        }
    }
}
