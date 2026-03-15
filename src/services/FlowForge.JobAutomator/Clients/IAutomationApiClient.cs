using FlowForge.Contracts.Events;

namespace FlowForge.JobAutomator.Clients;

public interface IAutomationApiClient
{
    Task<IReadOnlyList<AutomationSnapshot>> GetSnapshotsAsync(CancellationToken ct);
}
