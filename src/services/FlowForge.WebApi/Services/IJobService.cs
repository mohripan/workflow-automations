using FlowForge.Domain.Repositories;
using FlowForge.WebApi.DTOs.Responses;

namespace FlowForge.WebApi.Services;

public interface IJobService
{
    Task<IEnumerable<JobResponse>> GetAllAsync(IJobRepository repo, Guid? automationId, CancellationToken ct);
    Task RequestCancelAsync(IJobRepository repo, Guid id, CancellationToken ct);
    Task RemoveAsync(IJobRepository repo, Guid id, CancellationToken ct);
}
