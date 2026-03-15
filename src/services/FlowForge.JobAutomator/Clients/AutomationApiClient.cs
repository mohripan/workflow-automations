using System.Net.Http.Json;
using FlowForge.Contracts.Events;

namespace FlowForge.JobAutomator.Clients;

public class AutomationApiClient(HttpClient httpClient) : IAutomationApiClient
{
    public async Task<IReadOnlyList<AutomationSnapshot>> GetSnapshotsAsync(CancellationToken ct)
    {
        return await httpClient.GetFromJsonAsync<IReadOnlyList<AutomationSnapshot>>("api/automations/snapshots", ct) ?? [];
    }
}
