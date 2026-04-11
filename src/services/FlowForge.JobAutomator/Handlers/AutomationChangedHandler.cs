using FlowForge.Contracts.Events;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.JobAutomator.Cache;
using FlowForge.JobAutomator.Initialization;

namespace FlowForge.JobAutomator.Handlers;

public class AutomationChangedHandler(
    AutomationCache cache,
    IQuartzScheduleSync scheduleSync,
    ILogger<AutomationChangedHandler> logger) : IEventHandler<AutomationChangedEvent>
{
    public async Task HandleAsync(AutomationChangedEvent @event, CancellationToken ct)
    {
        try
        {
            switch (@event.ChangeType)
            {
                case ChangeType.Created:
                case ChangeType.Updated:
                    if (@event.Automation != null)
                    {
                        cache.Upsert(@event.Automation);
                        await scheduleSync.SyncAsync(@event.Automation, ct);
                        logger.LogInformation(
                            "Cache updated for automation {AutomationId} ({ChangeType}, IsEnabled={IsEnabled})",
                            @event.AutomationId, @event.ChangeType, @event.Automation.IsEnabled);
                    }
                    break;
                case ChangeType.Deleted:
                    cache.Remove(@event.AutomationId);
                    await scheduleSync.RemoveAllAsync(@event.AutomationId, ct);
                    logger.LogInformation("Automation {AutomationId} removed from cache", @event.AutomationId);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing automation changed event for {AutomationId}", @event.AutomationId);
        }
    }
}
