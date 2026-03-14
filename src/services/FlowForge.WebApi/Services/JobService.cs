using FlowForge.Domain.Enums;
using FlowForge.Domain.Exceptions;
using FlowForge.Domain.Repositories;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Contracts.Events;
using FlowForge.WebApi.DTOs.Responses;

namespace FlowForge.WebApi.Services;

public class JobService(
    IMessagePublisher publisher,
    IAutomationRepository automationRepo) : IJobService
{
    public async Task<IEnumerable<JobResponse>> GetAllAsync(IJobRepository repo, Guid? automationId, CancellationToken ct)
    {
        var jobs = await repo.GetAllAsync(automationId, ct);
        var responses = new List<JobResponse>();
        
        foreach (var job in jobs)
        {
            var automation = await automationRepo.GetByIdAsync(job.AutomationId, ct);
            responses.Add(new JobResponse(
                job.Id, job.AutomationId, automation?.Name ?? "Unknown", job.HostGroupId,
                job.HostId, job.Status, job.Message, job.CreatedAt, job.UpdatedAt
            ));
        }
        
        return responses;
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
            ), null, ct);

        }
    }

    public async Task RemoveAsync(IJobRepository repo, Guid id, CancellationToken ct)
    {
        var job = await repo.GetByIdAsync(id, ct) ?? throw new JobNotFoundException(id);

        if (job.Status != JobStatus.Pending)
            throw new DomainException("Only pending jobs can be removed via DELETE. Use POST /cancel for running jobs.");

        job.Transition(JobStatus.Removed);
        await repo.SaveAsync(job, ct);
    }
}
