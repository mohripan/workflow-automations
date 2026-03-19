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
    private static readonly int[] BackoffSeconds = [2, 4, 8, 16, 30];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Initializing AutomationCache...");

        using var scope = scopeFactory.CreateScope();
        var apiClient = scope.ServiceProvider.GetRequiredService<IAutomationApiClient>();

        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var snapshots = await apiClient.GetSnapshotsAsync(cancellationToken);
                foreach (var snapshot in snapshots)
                {
                    cache.Upsert(snapshot);
                    await scheduleSync.SyncAsync(snapshot, cancellationToken);
                }
                logger.LogInformation("AutomationCache initialized with {Count} automations.", snapshots.Count);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var delay = BackoffSeconds[Math.Min(attempt, BackoffSeconds.Length - 1)];
                logger.LogWarning(ex,
                    "AutomationCache initialization failed (attempt {Attempt}); retrying in {Delay}s.",
                    attempt + 1, delay);
                attempt++;

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
