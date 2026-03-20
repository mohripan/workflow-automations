using FlowForge.Domain.Entities;
using Xunit;
using FlowForge.Domain.Enums;
using FlowForge.Domain.Exceptions;
using FluentAssertions;

namespace FlowForge.Domain.Tests;

public class JobStateMachineTests
{
    // ── Valid transitions ────────────────────────────────────────────────────

    [Theory]
    [InlineData(JobStatus.Pending,   JobStatus.Started)]
    [InlineData(JobStatus.Pending,   JobStatus.Removed)]
    [InlineData(JobStatus.Pending,   JobStatus.Cancel)]
    [InlineData(JobStatus.Started,   JobStatus.InProgress)]
    [InlineData(JobStatus.Started,   JobStatus.Cancel)]
    [InlineData(JobStatus.InProgress, JobStatus.Completed)]
    [InlineData(JobStatus.InProgress, JobStatus.Error)]
    [InlineData(JobStatus.InProgress, JobStatus.Cancel)]
    [InlineData(JobStatus.Cancel,    JobStatus.Cancelled)]
    [InlineData(JobStatus.Cancel,    JobStatus.Error)]
    public void Transition_ValidPath_Succeeds(JobStatus from, JobStatus to)
    {
        var job = CreateJobAt(from);
        var act = () => job.Transition(to);

        act.Should().NotThrow();
        job.Status.Should().Be(to);
    }

    // ── Invalid transitions ───────────────────────────────────────────────────

    [Theory]
    [InlineData(JobStatus.Pending,   JobStatus.Completed)]
    [InlineData(JobStatus.Pending,   JobStatus.InProgress)]
    [InlineData(JobStatus.Pending,   JobStatus.Error)]
    [InlineData(JobStatus.Pending,   JobStatus.Cancelled)]
    [InlineData(JobStatus.Started,   JobStatus.Completed)]
    [InlineData(JobStatus.Started,   JobStatus.Removed)]
    [InlineData(JobStatus.InProgress, JobStatus.Started)]
    [InlineData(JobStatus.InProgress, JobStatus.Pending)]
    [InlineData(JobStatus.Completed, JobStatus.Started)]
    [InlineData(JobStatus.Completed, JobStatus.Error)]
    [InlineData(JobStatus.Cancelled, JobStatus.Started)]
    [InlineData(JobStatus.Error,     JobStatus.Started)]
    [InlineData(JobStatus.Removed,   JobStatus.Pending)]
    [InlineData(JobStatus.Cancel,    JobStatus.Pending)]
    [InlineData(JobStatus.Cancel,    JobStatus.InProgress)]
    public void Transition_InvalidPath_ThrowsInvalidJobTransitionException(JobStatus from, JobStatus to)
    {
        var job = CreateJobAt(from);
        var act = () => job.Transition(to);

        act.Should().Throw<InvalidJobTransitionException>();
    }

    // ── IsTerminal ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(JobStatus.Completed,             true)]
    [InlineData(JobStatus.CompletedUnsuccessfully, true)]
    [InlineData(JobStatus.Error,                 true)]
    [InlineData(JobStatus.Cancelled,             true)]
    [InlineData(JobStatus.Removed,               true)]
    [InlineData(JobStatus.Pending,               false)]
    [InlineData(JobStatus.Started,               false)]
    [InlineData(JobStatus.InProgress,            false)]
    [InlineData(JobStatus.Cancel,                false)]
    public void IsTerminal_ReturnsExpected(JobStatus status, bool expected)
    {
        status.IsTerminal().Should().Be(expected);
    }

    // ── IsCancellable ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(JobStatus.Pending,   true)]
    [InlineData(JobStatus.Started,   true)]
    [InlineData(JobStatus.InProgress, true)]
    [InlineData(JobStatus.Completed, false)]
    [InlineData(JobStatus.Error,     false)]
    [InlineData(JobStatus.Cancelled, false)]
    [InlineData(JobStatus.Removed,   false)]
    [InlineData(JobStatus.Cancel,    false)]
    public void IsCancellable_ReturnsExpected(JobStatus status, bool expected)
    {
        status.IsCancellable().Should().Be(expected);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a Job and force-transitions it to the desired status by following
    /// a valid path. This avoids duplicating the state machine just to set up tests.
    /// </summary>
    private static Job CreateJobAt(JobStatus target)
    {
        var job = Job.Create(Guid.NewGuid(), "task-1", "conn-1", Guid.NewGuid(), DateTimeOffset.UtcNow);

        // Each case follows a valid path from Pending → target
        switch (target)
        {
            case JobStatus.Pending:
                break;
            case JobStatus.Started:
                job.Transition(JobStatus.Started);
                break;
            case JobStatus.InProgress:
                job.Transition(JobStatus.Started);
                job.Transition(JobStatus.InProgress);
                break;
            case JobStatus.Completed:
                job.Transition(JobStatus.Started);
                job.Transition(JobStatus.InProgress);
                job.Transition(JobStatus.Completed);
                break;
            case JobStatus.CompletedUnsuccessfully:
                // No direct path via state machine (produced by engine directly).
                // Use reflection to set the status for testing IsTerminal.
                SetStatus(job, JobStatus.CompletedUnsuccessfully);
                break;
            case JobStatus.Error:
                job.Transition(JobStatus.Started);
                job.Transition(JobStatus.InProgress);
                job.Transition(JobStatus.Error);
                break;
            case JobStatus.Cancel:
                job.Transition(JobStatus.Cancel);
                break;
            case JobStatus.Cancelled:
                job.Transition(JobStatus.Cancel);
                job.Transition(JobStatus.Cancelled);
                break;
            case JobStatus.Removed:
                job.Transition(JobStatus.Removed);
                break;
        }

        return job;
    }

    private static void SetStatus(Job job, JobStatus status)
    {
        typeof(Job)
            .GetProperty(nameof(Job.Status))!
            .SetValue(job, status);
    }
}
