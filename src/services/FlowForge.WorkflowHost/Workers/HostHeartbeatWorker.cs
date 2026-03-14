using FlowForge.Infrastructure.Caching;

namespace FlowForge.WorkflowHost.Workers;

public class HostHeartbeatWorker(
    IRedisService redis,
    ILogger<HostHeartbeatWorker> logger) : BackgroundService
{
    private readonly string _hostId = Environment.GetEnvironmentVariable("NODE_NAME") ?? Environment.MachineName;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await redis.SetAsync($"host:heartbeat:{_hostId}", "online", TimeSpan.FromSeconds(30));
                logger.LogTrace("Sent heartbeat for host {HostId}", _hostId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send heartbeat for host {HostId}", _hostId);
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
