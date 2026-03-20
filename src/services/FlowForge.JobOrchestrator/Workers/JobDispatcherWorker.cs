using System.Diagnostics;
using FlowForge.Contracts.Events;
using FlowForge.Domain.Enums;
using FlowForge.Domain.Repositories;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.DeadLetter;
using FlowForge.Infrastructure.Messaging.Redis;
using System.Text.Json;
using FlowForge.JobOrchestrator.LoadBalancing;

namespace FlowForge.JobOrchestrator.Workers;

public class JobDispatcherWorker(
    IMessageConsumer consumer,
    IMessagePublisher publisher,
    IWorkflowHostRepository hostRepo,
    IServiceProvider serviceProvider,
    ILoadBalancer loadBalancer,
    IDlqWriter dlqWriter,
    ILogger<JobDispatcherWorker> logger) : BackgroundService
{
    private static readonly ActivitySource _activitySource = new("FlowForge.JobOrchestrator");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var @event in consumer.ConsumeAsync<JobCreatedEvent>(
            StreamNames.JobCreated, "job-orchestrator", "orchestrator-1", stoppingToken))
        {
            try
            {
                using var activity = _activitySource.StartActivity($"dispatch job {@event.JobId}");
                var jobRepo = serviceProvider.GetRequiredKeyedService<IJobRepository>(@event.ConnectionId);
                var job = await jobRepo.GetByIdAsync(@event.JobId, stoppingToken);

                if (job == null)
                {
                    logger.LogWarning("Job {JobId} not found in connection {ConnectionId}", @event.JobId, @event.ConnectionId);
                    continue;
                }

                var hosts = await hostRepo.GetByGroupAsync(job.HostGroupId, stoppingToken);
                var onlineHosts = hosts.Where(h => h.IsOnline).ToList();

                if (onlineHosts.Count == 0)
                {
                    logger.LogWarning("No online hosts in group {HostGroupId} for job {JobId}", job.HostGroupId, job.Id);
                    continue;
                }

                var selectedHost = loadBalancer.Select(onlineHosts, job.HostGroupId);

                job.Transition(JobStatus.Started);
                job.AssignToHost(selectedHost.Id);
                await jobRepo.SaveAsync(job, stoppingToken);

                await publisher.PublishAsync(new JobAssignedEvent(
                    JobId: job.Id,
                    ConnectionId: @event.ConnectionId,
                    HostId: selectedHost.Id,
                    AutomationId: job.AutomationId,
                    AssignedAt: DateTimeOffset.UtcNow
                ), StreamNames.HostStream(selectedHost.Id.ToString()), stoppingToken);

                logger.LogInformation("Job {JobId} assigned to host {HostId}", job.Id, selectedHost.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error dispatching job {JobId}. Sending to DLQ.", @event.JobId);
                await dlqWriter.WriteAsync(
                    sourceStream: StreamNames.JobCreated,
                    messageId: @event.JobId.ToString(),
                    payload: JsonSerializer.Serialize(@event),
                    error: ex.Message);
            }
        }
    }
}
