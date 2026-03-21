using FlowForge.Domain.Enums;
using FlowForge.Domain.Exceptions;
using FlowForge.Domain.Repositories;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Contracts.Events;
using FlowForge.WebApi.DTOs.Requests;
using FlowForge.WebApi.DTOs.Responses;

namespace FlowForge.WebApi.Services;

public class JobService(
    IMessagePublisher publisher,
    IAutomationRepository automationRepo) : IJobService
{
    public async Task<PagedResult<JobResponse>> GetAllAsync(IJobRepository repo, JobQueryParams query, CancellationToken ct)
    {
        var jobs = await repo.GetAllAsync(query.AutomationId, ct);

        var filtered = jobs.AsQueryable();
        if (query.Status.HasValue)
            filtered = filtered.Where(j => j.Status == query.Status.Value);

        var total = filtered.Count();
        var items = new List<JobResponse>();

        var pagedJobs = filtered
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToList();

        foreach (var job in pagedJobs)
        {
            var automation = await automationRepo.GetByIdAsync(job.AutomationId, ct);
            items.Add(new JobResponse(
                job.Id, job.AutomationId, automation?.Name ?? "Unknown", job.HostGroupId,
                job.HostId, job.Status, job.Message, job.OutputJson, job.CreatedAt, job.UpdatedAt
            ));
        }

        return new PagedResult<JobResponse>(items, total, query.Page, query.PageSize);
    }

    public async Task<JobResponse> GetByIdAsync(IJobRepository repo, Guid id, CancellationToken ct)
    {
        var job = await repo.GetByIdAsync(id, ct) ?? throw new JobNotFoundException(id);
        var automation = await automationRepo.GetByIdAsync(job.AutomationId, ct);

        return new JobResponse(
            job.Id, job.AutomationId, automation?.Name ?? "Unknown", job.HostGroupId,
            job.HostId, job.Status, job.Message, job.OutputJson, job.CreatedAt, job.UpdatedAt
        );
    }

    public async Task RequestCancelAsync(IJobRepository repo, Guid id, CancellationToken ct)
    {
        var job = await repo.GetByIdAsync(id, ct) ?? throw new JobNotFoundException(id);

        if (job.Status == JobStatus.Pending)
        {
            job.Transition(JobStatus.Removed);
            await repo.SaveAsync(job, ct);
            return;
        }

        if (!job.Status.IsCancellable())
            throw new InvalidJobTransitionException(job.Status, JobStatus.Cancel);

        job.Transition(JobStatus.Cancel);
        await repo.SaveAsync(job, ct);

        if (job.HostId.HasValue)
        {
            await publisher.PublishAsync(new JobCancelRequestedEvent(
                JobId: job.Id,
                HostId: job.HostId!.Value,
                RequestedAt: DateTimeOffset.UtcNow
            ), ct: ct);
        }
    }

    public async Task RemoveAsync(IJobRepository repo, Guid id, CancellationToken ct)
    {
        var job = await repo.GetByIdAsync(id, ct) ?? throw new JobNotFoundException(id);

        if (job.Status != JobStatus.Pending)
            throw new InvalidAutomationException("Only pending jobs can be removed via DELETE. Use POST /cancel for running jobs.");

        job.Transition(JobStatus.Removed);
        await repo.SaveAsync(job, ct);
    }
}
