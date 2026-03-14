using FlowForge.WebApi.DTOs.Responses;
using Microsoft.AspNetCore.SignalR;

namespace FlowForge.WebApi.Hubs;

public interface IJobStatusClient
{
    Task OnJobStatusChanged(JobStatusUpdate update);
}

public class JobStatusHub : Hub<IJobStatusClient>
{
    public async Task SubscribeToJob(Guid jobId)
        => await Groups.AddToGroupAsync(Context.ConnectionId, $"job:{jobId}");

    public async Task UnsubscribeFromJob(Guid jobId)
        => await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"job:{jobId}");
}
