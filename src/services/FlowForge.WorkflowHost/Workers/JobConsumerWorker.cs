using System.Collections.Concurrent;
using FlowForge.Contracts.Events;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.Redis;
using FlowForge.WorkflowHost.ProcessManagement;

namespace FlowForge.WorkflowHost.Workers;

public class JobConsumerWorker(
    IMessageConsumer consumer,
    IProcessManager processManager,
    ILogger<JobConsumerWorker> logger) : BackgroundService
{
    private readonly string _hostId = Environment.GetEnvironmentVariable("NODE_NAME") ?? Environment.MachineName;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeJobs = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var @event in consumer.ConsumeAsync<JobAssignedEvent>(
            StreamNames.HostStream(_hostId), "workflow-host", _hostId, stoppingToken))
        {
            _ = RunJobAsync(@event, stoppingToken);
        }
    }

    private async Task RunJobAsync(JobAssignedEvent @event, CancellationToken hostStopping)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(hostStopping);
        _activeJobs[@event.JobId] = cts;

        try
        {
            logger.LogInformation("Job {JobId} starting on host {HostId}", @event.JobId, _hostId);
            await processManager.RunAsync(@event.JobId, @event.ConnectionId, cts.Token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error running job {JobId}", @event.JobId);
        }
        finally
        {
            _activeJobs.TryRemove(@event.JobId, out _);
        }
    }

    public bool TryCancel(Guid jobId)
    {
        if (_activeJobs.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
            return true;
        }
        return false;
    }
}
