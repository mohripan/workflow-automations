using FlowForge.Infrastructure.Caching;
using FlowForge.WorkflowHost.Options;
using Microsoft.Extensions.Options;

namespace FlowForge.WorkflowHost.Workers;

public class HostHeartbeatWorker(
    IRedisService redis,
    IOptions<HostHeartbeatOptions> options,
    ILogger<HostHeartbeatWorker> logger) : BackgroundService
{
    private readonly string _hostId = Environment.GetEnvironmentVariable("NODE_NAME") ?? Environment.MachineName;
    private readonly HostHeartbeatOptions _options = options.Value;

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

            await Task.Delay(TimeSpan.FromSeconds(_options.PublishIntervalSeconds), stoppingToken);
        }
    }
}
