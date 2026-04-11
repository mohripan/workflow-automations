using FlowForge.Contracts.Events;
using FlowForge.Infrastructure.Messaging.Abstractions;

namespace FlowForge.WorkflowHost.Handlers;

public class JobCancelRequestedHandler(
    JobAssignedHandler jobHandler,
    ILogger<JobCancelRequestedHandler> logger) : IEventHandler<JobCancelRequestedEvent>
{
    private readonly string _hostId = Environment.GetEnvironmentVariable("NODE_NAME") ?? Environment.MachineName;

    public Task HandleAsync(JobCancelRequestedEvent @event, CancellationToken ct)
    {
        if (@event.HostId.ToString() == _hostId || @event.HostId == Guid.Empty)
        {
            if (jobHandler.TryCancel(@event.JobId))
            {
                logger.LogInformation("Successfully cancelled job {JobId}", @event.JobId);
            }
            else
            {
                logger.LogDebug("Received cancel request for job {JobId} but it's not active on this host", @event.JobId);
            }
        }

        return Task.CompletedTask;
    }
}
