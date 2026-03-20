using FlowForge.Contracts.Events;
using FlowForge.Domain.Enums;
using FlowForge.Domain.Repositories;
using FlowForge.Infrastructure.Caching;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.JobOrchestrator.Options;
using Microsoft.Extensions.Options;

namespace FlowForge.JobOrchestrator.Workers;

public class HeartbeatMonitorWorker(
    IWorkflowHostRepository hostRepo,
    IRedisService redis,
    IOptions<HeartbeatMonitorOptions> options,
    ILogger<HeartbeatMonitorWorker> logger) : BackgroundService
{
    private readonly HeartbeatMonitorOptions _options = options.Value;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var hosts = await hostRepo.GetAllAsync(stoppingToken);

                foreach (var host in hosts)
                {
                    var isAlive = await redis.GetAsync($"host:heartbeat:{host.Id}") != null;
                    
                    if (!isAlive && host.IsOnline)
                    {
                        logger.LogWarning("Host {HostId} heartbeat expired, marking offline", host.Id);
                        host.MarkOffline();
                        await hostRepo.SaveAsync(host, stoppingToken);
                    }
                    else if (isAlive && !host.IsOnline)
                    {
                        logger.LogInformation("Host {HostId} heartbeat detected, marking online", host.Id);
                        host.MarkOnline();
                        await hostRepo.SaveAsync(host, stoppingToken);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in HeartbeatMonitorWorker");
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.CheckIntervalSeconds), stoppingToken);
        }
    }
}
