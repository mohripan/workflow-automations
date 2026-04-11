using System.Collections.Concurrent;
using FlowForge.Contracts.Events;
using FlowForge.Infrastructure.Messaging;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.DeadLetter;
using FlowForge.WorkflowHost.ProcessManagement;
using System.Text.Json;

namespace FlowForge.WorkflowHost.Handlers;

public class JobAssignedHandler(
    IProcessManager processManager,
    IDlqWriter dlqWriter,
    ILogger<JobAssignedHandler> logger) : IEventHandler<JobAssignedEvent>
{
    private readonly string _hostId = Environment.GetEnvironmentVariable("NODE_NAME") ?? Environment.MachineName;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeJobs = new();

    public async Task HandleAsync(JobAssignedEvent @event, CancellationToken ct)
    {
        _ = RunJobAsync(@event, ct);
        await Task.CompletedTask;
    }

    private async Task RunJobAsync(JobAssignedEvent @event, CancellationToken hostStopping)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(hostStopping);
        _activeJobs[@event.JobId] = cts;

        try
        {
            logger.LogInformation("Job {JobId} starting on host {HostId}", @event.JobId, _hostId);
            await processManager.RunAsync(@event.JobId, @event.AutomationId, @event.ConnectionId, cts.Token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error running job {JobId}. Sending to DLQ.", @event.JobId);
            await dlqWriter.WriteAsync(
                sourceStream: TopicNames.HostTopic(_hostId),
                messageId: @event.JobId.ToString(),
                payload: JsonSerializer.Serialize(@event),
                error: ex.Message);
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
