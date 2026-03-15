using FlowForge.Domain.Repositories;
using FlowForge.WebApi.DTOs.Requests;
using FlowForge.WebApi.DTOs.Responses;

namespace FlowForge.WebApi.Services;

public interface IJobService
{
    Task<PagedResult<JobResponse>> GetAllAsync(IJobRepository repo, JobQueryParams query, CancellationToken ct);
    Task<JobResponse> GetByIdAsync(IJobRepository repo, Guid id, CancellationToken ct);
    Task RequestCancelAsync(IJobRepository repo, Guid id, CancellationToken ct);
    Task RemoveAsync(IJobRepository repo, Guid id, CancellationToken ct);
}
