using FlowForge.Contracts.Events;
using FlowForge.Domain.Enums;
using FlowForge.Infrastructure.Caching;
using FlowForge.Infrastructure.Messaging.Abstractions;

namespace FlowForge.WorkflowEngine.Reporting;

public interface IJobReporter
{
    Task ReportStatusAsync(Guid jobId, string connectionId, JobStatus status, string? message = null, CancellationToken ct = default);
    Task RefreshHeartbeatAsync(Guid jobId, CancellationToken ct = default);
}

public class JobProgressReporter(IMessagePublisher publisher, IRedisService redis) : IJobReporter
{
    public async Task ReportStatusAsync(Guid jobId, string connectionId, JobStatus status, string? message = null, CancellationToken ct = default)
    {
        await publisher.PublishAsync(new JobStatusChangedEvent(
            JobId: jobId,
            ConnectionId: connectionId,
            Status: status,
            Message: message,
            UpdatedAt: DateTimeOffset.UtcNow
        ), null, ct);
    }

    public async Task RefreshHeartbeatAsync(Guid jobId, CancellationToken ct = default)
    {
        await redis.SetAsync($"heartbeat:{jobId}", "alive", TimeSpan.FromSeconds(30));
    }
}
