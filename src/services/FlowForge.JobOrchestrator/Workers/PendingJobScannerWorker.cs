using FlowForge.Contracts.Events;
using FlowForge.Domain.Repositories;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.Redis;
using FlowForge.JobOrchestrator.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FlowForge.JobOrchestrator.Workers;

public class PendingJobScannerWorker(
    IServiceScopeFactory scopeFactory,
    IMessagePublisher publisher,
    IConfiguration configuration,
    IOptions<PendingJobScannerOptions> options,
    ILogger<PendingJobScannerWorker> logger) : BackgroundService
{
    private readonly PendingJobScannerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(_options.ScanIntervalSeconds), stoppingToken);

            try
            {
                var connectionIds = configuration.GetSection("JobConnections")
                    .GetChildren()
                    .Select(s => s.Key)
                    .ToList();

                var staleThreshold = DateTimeOffset.UtcNow.AddSeconds(-_options.StaleAfterSeconds);

                foreach (var connectionId in connectionIds)
                {
                    using var scope = scopeFactory.CreateScope();
                    var jobRepo = scope.ServiceProvider.GetRequiredKeyedService<IJobRepository>(connectionId);
                    var pendingJobs = await jobRepo.GetPendingAsync(stoppingToken);
                    var staleJobs = pendingJobs.Where(j => j.CreatedAt < staleThreshold).ToList();

                    foreach (var job in staleJobs)
                    {
                        logger.LogInformation(
                            "Re-queuing stale pending job {JobId} (connection {ConnectionId}, age {AgeSeconds:F0}s)",
                            job.Id, connectionId, (DateTimeOffset.UtcNow - job.CreatedAt).TotalSeconds);

                        await publisher.PublishAsync(new JobCreatedEvent(
                            JobId: job.Id,
                            ConnectionId: connectionId,
                            AutomationId: job.AutomationId,
                            HostGroupId: job.HostGroupId,
                            CreatedAt: job.CreatedAt,
                            TimeoutSeconds: job.TimeoutSeconds
                        ), StreamNames.JobCreated, stoppingToken);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in PendingJobScannerWorker");
            }
        }
    }
}
