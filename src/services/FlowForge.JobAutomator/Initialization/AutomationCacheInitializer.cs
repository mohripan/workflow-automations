using FlowForge.JobAutomator.Cache;
using FlowForge.JobAutomator.Clients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowForge.JobAutomator.Initialization;

public class AutomationCacheInitializer(
    IServiceScopeFactory scopeFactory,
    AutomationCache cache,
    IQuartzScheduleSync scheduleSync,
    ILogger<AutomationCacheInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Initializing AutomationCache...");
        
        using var scope = scopeFactory.CreateScope();
        var apiClient = scope.ServiceProvider.GetRequiredService<IAutomationApiClient>();
        
        try
        {
            var snapshots = await apiClient.GetSnapshotsAsync(cancellationToken);
            foreach (var snapshot in snapshots)
            {
                cache.Upsert(snapshot);
                await scheduleSync.SyncAsync(snapshot, cancellationToken);
            }
            logger.LogInformation("AutomationCache initialized with {Count} snapshots.", snapshots.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize AutomationCache.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
