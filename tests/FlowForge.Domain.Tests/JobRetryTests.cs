using FlowForge.Domain.Entities;
using FlowForge.Domain.Enums;
using FluentAssertions;

namespace FlowForge.Domain.Tests;

public class JobRetryTests
{
    [Fact]
    public void Create_DefaultRetryValues_AreZero()
    {
        var job = Job.Create(Guid.NewGuid(), "run-script", "conn-1", Guid.NewGuid(), DateTimeOffset.UtcNow);

        job.RetryAttempt.Should().Be(0);
        job.MaxRetries.Should().Be(0);
    }

    [Fact]
    public void Create_WithRetryAttemptAndMaxRetries_StoresBoth()
    {
        var job = Job.Create(
            Guid.NewGuid(), "run-script", "conn-1", Guid.NewGuid(), DateTimeOffset.UtcNow,
            retryAttempt: 1, maxRetries: 3);

        job.RetryAttempt.Should().Be(1);
        job.MaxRetries.Should().Be(3);
    }

    [Fact]
    public void Create_WithTimeoutSeconds_StoresValue()
    {
        var job = Job.Create(
            Guid.NewGuid(), "run-script", "conn-1", Guid.NewGuid(), DateTimeOffset.UtcNow,
            timeoutSeconds: 300);

        job.TimeoutSeconds.Should().Be(300);
    }

    [Fact]
    public void Create_WithoutTimeout_HasNullTimeoutSeconds()
    {
        var job = Job.Create(Guid.NewGuid(), "run-script", "conn-1", Guid.NewGuid(), DateTimeOffset.UtcNow);

        job.TimeoutSeconds.Should().BeNull();
    }

    [Fact]
    public void Create_WithTaskConfig_StoresConfig()
    {
        const string config = """{"scriptPath":"/opt/scripts/process.py"}""";
        var job = Job.Create(
            Guid.NewGuid(), "run-script", "conn-1", Guid.NewGuid(), DateTimeOffset.UtcNow,
            taskConfig: config);

        job.TaskConfig.Should().Be(config);
    }

    [Fact]
    public void Create_WithoutTaskConfig_HasNullTaskConfig()
    {
        var job = Job.Create(Guid.NewGuid(), "run-script", "conn-1", Guid.NewGuid(), DateTimeOffset.UtcNow);

        job.TaskConfig.Should().BeNull();
    }

    [Fact]
    public void Create_AlwaysStartsInPendingStatus()
    {
        var job = Job.Create(Guid.NewGuid(), "run-script", "conn-1", Guid.NewGuid(), DateTimeOffset.UtcNow);

        job.Status.Should().Be(JobStatus.Pending);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 1, true)]
    [InlineData(1, 3, true)]
    [InlineData(2, 3, true)]
    [InlineData(3, 3, false)]
    public void RetryAttempt_LessThanMaxRetries_HasRetriesRemaining(int attempt, int maxRetries, bool expected)
    {
        var job = Job.Create(
            Guid.NewGuid(), "run-script", "conn-1", Guid.NewGuid(), DateTimeOffset.UtcNow,
            retryAttempt: attempt, maxRetries: maxRetries);

        (job.RetryAttempt < job.MaxRetries).Should().Be(expected);
    }

    [Fact]
    public void Create_StoresConnectionIdAndTaskId()
    {
        var automationId = Guid.NewGuid();
        var hostGroupId = Guid.NewGuid();

        var job = Job.Create(automationId, "run-script", "conn-xyz", hostGroupId, DateTimeOffset.UtcNow);

        job.AutomationId.Should().Be(automationId);
        job.TaskId.Should().Be("run-script");
        job.ConnectionId.Should().Be("conn-xyz");
        job.HostGroupId.Should().Be(hostGroupId);
    }

    [Fact]
    public void Create_StoresTriggeredAt()
    {
        var triggeredAt = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

        var job = Job.Create(Guid.NewGuid(), "run-script", "conn-1", Guid.NewGuid(), triggeredAt);

        job.TriggeredAt.Should().Be(triggeredAt);
    }
}
