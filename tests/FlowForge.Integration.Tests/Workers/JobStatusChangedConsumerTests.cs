using FlowForge.Contracts.Events;
using FlowForge.Domain.Entities;
using FlowForge.Domain.Enums;
using FlowForge.Domain.Repositories;
using FlowForge.Domain.ValueObjects;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.DeadLetter;
using FlowForge.Infrastructure.Persistence.Platform;
using FlowForge.Infrastructure.Repositories;
using FlowForge.Integration.Tests.Infrastructure;
using FlowForge.WebApi.DTOs.Responses;
using FlowForge.WebApi.Hubs;
using FlowForge.WebApi.Workers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FlowForge.Integration.Tests.Workers;

[Collection("Containers")]
public class JobStatusChangedConsumerTests : IAsyncLifetime
{
    private readonly SharedContainersFixture _fixture;
    private PlatformDbContext _platformDb = null!;
    private IDbContextTransaction _platformTx = null!;

    public JobStatusChangedConsumerTests(SharedContainersFixture fixture)
        => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _platformDb = _fixture.CreatePlatformDbContext();
        _platformTx = await _platformDb.Database.BeginTransactionAsync();
    }

    public async Task DisposeAsync()
    {
        await _platformTx.RollbackAsync();
        await _platformDb.DisposeAsync();
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenNonTerminalStatusReceived_TransitionsJobAndKeepsActiveJobId()
    {
        // Arrange
        var (automation, connectionId, job) = await SeedJobAsync(JobStatus.Pending);
        automation.SetActiveJob(job.Id);
        await _platformDb.SaveChangesAsync();

        using var jobsDb = _fixture.CreateJobsDbContext();
        using var jobsTx = await jobsDb.Database.BeginTransactionAsync();

        var @event = new JobStatusChangedEvent(
            JobId: job.Id,
            AutomationId: automation.Id,
            ConnectionId: connectionId,
            Status: JobStatus.Started,
            Message: null,
            UpdatedAt: DateTimeOffset.UtcNow);

        // Act
        await RunConsumerAsync(new FakeMessageConsumer(@event), connectionId, jobsDb);

        // Assert — job transitioned
        await jobsDb.Entry(job).ReloadAsync();
        job.Status.Should().Be(JobStatus.Started);

        // Assert — ActiveJobId NOT cleared (non-terminal status)
        await _platformDb.Entry(automation).ReloadAsync();
        automation.ActiveJobId.Should().Be(job.Id);

        await jobsTx.RollbackAsync();
    }

    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Error)]
    [InlineData(JobStatus.Cancelled)]
    public async Task WhenTerminalStatusReceived_TransitionsJobAndClearsActiveJobId(JobStatus terminalStatus)
    {
        // Arrange — job must be at a state from which terminalStatus is reachable
        var startStatus = terminalStatus switch
        {
            JobStatus.Completed or JobStatus.Error => JobStatus.InProgress,
            JobStatus.Cancelled => JobStatus.Cancel,
            _ => JobStatus.InProgress
        };

        var (automation, connectionId, job) = await SeedJobAsync(startStatus);
        automation.SetActiveJob(job.Id);
        await _platformDb.SaveChangesAsync();

        using var jobsDb = _fixture.CreateJobsDbContext();
        using var jobsTx = await jobsDb.Database.BeginTransactionAsync();

        var @event = new JobStatusChangedEvent(
            JobId: job.Id,
            AutomationId: automation.Id,
            ConnectionId: connectionId,
            Status: terminalStatus,
            Message: "done",
            UpdatedAt: DateTimeOffset.UtcNow);

        // Act
        await RunConsumerAsync(new FakeMessageConsumer(@event), connectionId, jobsDb);

        // Assert — job transitioned and message set
        await jobsDb.Entry(job).ReloadAsync();
        job.Status.Should().Be(terminalStatus);
        job.Message.Should().Be("done");

        // Assert — ActiveJobId cleared
        await _platformDb.Entry(automation).ReloadAsync();
        automation.ActiveJobId.Should().BeNull();

        await jobsTx.RollbackAsync();
    }

    [Fact]
    public async Task WhenJobNotFound_LogsWarningAndContinues()
    {
        // Arrange — event for a job that does not exist in the DB
        var connectionId = "conn-" + Guid.NewGuid().ToString("N")[..8];
        using var jobsDb = _fixture.CreateJobsDbContext();
        using var jobsTx = await jobsDb.Database.BeginTransactionAsync();

        var @event = new JobStatusChangedEvent(
            JobId: Guid.NewGuid(),
            AutomationId: Guid.NewGuid(),
            ConnectionId: connectionId,
            Status: JobStatus.Completed,
            Message: null,
            UpdatedAt: DateTimeOffset.UtcNow);

        // Act — should not throw
        var act = async () => await RunConsumerAsync(new FakeMessageConsumer(@event), connectionId, jobsDb);

        await act.Should().NotThrowAsync();

        await jobsTx.RollbackAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<(Automation automation, string connectionId, Job job)> SeedJobAsync(JobStatus jobStatus)
    {
        var connectionId = "conn-" + Guid.NewGuid().ToString("N")[..8];
        var trigger = Trigger.Create("t1", "schedule", "{}");
        var automation = Automation.Create("Auto", null, "task-1", Guid.NewGuid(), [trigger],
            new TriggerConditionNode(null, "t1", null));
        await _platformDb.Automations.AddAsync(automation);
        await _platformDb.SaveChangesAsync();

        var job = Job.Create(automation.Id, automation.TaskId, connectionId, automation.HostGroupId, DateTimeOffset.UtcNow);
        AdvanceJobTo(job, jobStatus);

        using var jobsDb = _fixture.CreateJobsDbContext();
        await jobsDb.Jobs.AddAsync(job);
        await jobsDb.SaveChangesAsync();

        return (automation, connectionId, job);
    }

    private static void AdvanceJobTo(Job job, JobStatus target)
    {
        if (target == JobStatus.Pending) return;

        var path = new List<JobStatus>();
        if (target is JobStatus.Started or JobStatus.InProgress or JobStatus.Completed or JobStatus.Error)
        {
            path.Add(JobStatus.Started);
            if (target != JobStatus.Started) path.Add(JobStatus.InProgress);
            if (target is JobStatus.Completed or JobStatus.Error) path.Add(target);
        }
        else if (target == JobStatus.Cancel) path.Add(JobStatus.Cancel);
        else if (target == JobStatus.Cancelled) { path.Add(JobStatus.Cancel); path.Add(JobStatus.Cancelled); }

        foreach (var s in path) job.Transition(s);
    }

    private async Task RunConsumerAsync(FakeMessageConsumer fakeConsumer, string connectionId,
        FlowForge.Infrastructure.Persistence.Jobs.JobsDbContext jobsDb)
    {
        var hubContext = Substitute.For<IHubContext<JobStatusHub, IJobStatusClient>>();
        var clients = Substitute.For<IHubClients<IJobStatusClient>>();
        var client = Substitute.For<IJobStatusClient>();
        hubContext.Clients.Returns(clients);
        clients.Group(Arg.Any<string>()).Returns(client);

        var services = new ServiceCollection();
        services.AddSingleton(_platformDb);
        services.AddScoped<IAutomationRepository, AutomationRepository>();
        services.AddKeyedSingleton<IJobRepository>(connectionId,
            (_, _) => new JobRepository(jobsDb));
        services.AddSingleton<IMessageConsumer>(fakeConsumer);
        services.AddLogging();

        var sp = services.BuildServiceProvider();

        var consumer = new TestableConsumer(
            fakeConsumer,
            sp,
            hubContext,
            NullLogger<JobStatusChangedConsumer>.Instance);

        await consumer.RunAsync(CancellationToken.None);
    }

    private sealed class TestableConsumer(
        IMessageConsumer consumer,
        IServiceProvider sp,
        IHubContext<JobStatusHub, IJobStatusClient> hubContext,
        Microsoft.Extensions.Logging.ILogger<JobStatusChangedConsumer> logger)
        : JobStatusChangedConsumer(consumer, sp.GetRequiredService<IServiceScopeFactory>(), hubContext, NSubstitute.Substitute.For<IDlqWriter>(), logger)
    {
        public Task RunAsync(CancellationToken ct) => ExecuteAsync(ct);
    }
}
