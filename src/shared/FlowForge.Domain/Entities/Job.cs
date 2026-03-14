using FlowForge.Domain.Enums;
using FlowForge.Domain.Exceptions;

namespace FlowForge.Domain.Entities;

public class Job : BaseEntity<Guid>
{
    public Guid AutomationId { get; private set; }
    public string TaskId { get; private set; } = default!;
    public string ParametersJson { get; private set; } = default!;
    public Guid HostGroupId { get; private set; }
    public Guid? HostId { get; private set; }
    public JobStatus Status { get; private set; }
    public string? Message { get; private set; }
    public DateTimeOffset? TriggeredAt { get; private set; }

    private Job() { }

    public static Job Create(Guid automationId, string taskId, string parametersJson, Guid hostGroupId, DateTimeOffset triggeredAt)
    {
        return new Job
        {
            Id = Guid.NewGuid(),
            AutomationId = automationId,
            TaskId = taskId,
            ParametersJson = parametersJson,
            HostGroupId = hostGroupId,
            Status = JobStatus.Pending,
            TriggeredAt = triggeredAt
        };
    }

    public void Transition(JobStatus next)
    {
        if (!IsValidTransition(Status, next))
            throw new InvalidJobTransitionException(Status, next);
        
        Status = next;
        UpdateTimestamp();
    }

    public void SetMessage(string message)
    {
        Message = message;
        UpdateTimestamp();
    }

    public void AssignToHost(Guid hostId)
    {
        HostId = hostId;
        UpdateTimestamp();
    }

    private static bool IsValidTransition(JobStatus current, JobStatus next)
    {
        // Simple state machine
        return (current, next) switch
        {
            (JobStatus.Pending, JobStatus.Started) => true,
            (JobStatus.Pending, JobStatus.Removed) => true,
            (JobStatus.Started, JobStatus.InProgress) => true,
            (JobStatus.InProgress, JobStatus.Completed) => true,
            (JobStatus.InProgress, JobStatus.Error) => true,
            (JobStatus.InProgress, JobStatus.Cancel) => true,
            (JobStatus.Started, JobStatus.Cancel) => true,
            (JobStatus.Pending, JobStatus.Cancel) => true,
            (JobStatus.Cancel, JobStatus.Cancelled) => true,
            (JobStatus.Cancel, JobStatus.Error) => true,
            _ => false
        };
    }
}
