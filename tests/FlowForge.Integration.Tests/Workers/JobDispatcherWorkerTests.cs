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
using FlowForge.JobOrchestrator.Handlers;
using FlowForge.JobOrchestrator.LoadBalancing;
using FlowForge.JobOrchestrator.Workers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FlowForge.Integration.Tests.Workers;

[Collection("Containers")]
public class JobDispatcherWorkerTests : IAsyncLifetime
{
    private readonly SharedContainersFixture _fixture;
    private PlatformDbContext _platformDb = null!;
    private IDbContextTransaction _platformTx = null!;

    public JobDispatcherWorkerTests(SharedContainersFixture fixture)
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
    public async Task WhenJobCreatedEventReceived_TransitionsJobToStartedAndPublishesAssigned()
    {
        // Arrange
        var (automation, hostGroup, connectionId, host) = await SeedAsync();
        using var jobsDb = _fixture.CreateJobsDbContext();
        using var jobsTx = await jobsDb.Database.BeginTransactionAsync();

        var job = Job.Create(automation.Id, automation.TaskId, connectionId, hostGroup.Id, DateTimeOffset.UtcNow);
        await jobsDb.Jobs.AddAsync(job);
        await jobsDb.SaveChangesAsync();

        JobAssignedEvent? captured = null;
        var publisher = Substitute.For<IMessagePublisher>();
        publisher
            .PublishAsync(Arg.Any<JobAssignedEvent>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => { captured = ci.ArgAt<JobAssignedEvent>(0); return Task.CompletedTask; });

        var @event = new JobCreatedEvent(
            job.Id, connectionId, automation.Id, hostGroup.Id, DateTimeOffset.UtcNow);

        var worker = BuildWorker(new FakeMessageConsumer(@event), publisher, connectionId, jobsDb);

        // Act
        await worker.RunAsync(CancellationToken.None);

        // Assert — job transitioned to Started and assigned to a host
        var updated = await jobsDb.Jobs.FindAsync(job.Id);
        updated!.Status.Should().Be(JobStatus.Started);
        updated.HostId.Should().NotBeNull("job must be assigned to a host");

        // Assert — JobAssignedEvent published with correct data
        captured.Should().NotBeNull();
        captured!.JobId.Should().Be(job.Id);
        captured.ConnectionId.Should().Be(connectionId);

        await jobsTx.RollbackAsync();
    }

    [Fact]
    public async Task WhenNoOnlineHosts_SkipsDispatchAndDoesNotPublish()
    {
        // Arrange — mark the host as offline
        var (automation, hostGroup, connectionId, host) = await SeedAsync();
        host.SetOffline();
        await _platformDb.SaveChangesAsync();

        using var jobsDb = _fixture.CreateJobsDbContext();
        using var jobsTx = await jobsDb.Database.BeginTransactionAsync();

        var job = Job.Create(automation.Id, automation.TaskId, connectionId, hostGroup.Id, DateTimeOffset.UtcNow);
        await jobsDb.Jobs.AddAsync(job);
        await jobsDb.SaveChangesAsync();

        var publisher = Substitute.For<IMessagePublisher>();
        var @event = new JobCreatedEvent(
            job.Id, connectionId, automation.Id, hostGroup.Id, DateTimeOffset.UtcNow);

        var worker = BuildWorker(new FakeMessageConsumer(@event), publisher, connectionId, jobsDb);

        // Act
        await worker.RunAsync(CancellationToken.None);

        // Assert — job stays Pending, publisher never called
        var updated = await jobsDb.Jobs.FindAsync(job.Id);
        updated!.Status.Should().Be(JobStatus.Pending, "no online host available — job stays pending");
        await publisher.DidNotReceive().PublishAsync(
            Arg.Any<JobAssignedEvent>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        await jobsTx.RollbackAsync();
    }

    [Fact]
    public async Task WhenJobNotFound_SkipsDispatchGracefully()
    {
        // Arrange — event references a job that does not exist in the DB
        var (automation, hostGroup, connectionId, _) = await SeedAsync();
        using var jobsDb = _fixture.CreateJobsDbContext();

        var publisher = Substitute.For<IMessagePublisher>();
        var @event = new JobCreatedEvent(
            Guid.NewGuid(), connectionId, automation.Id, hostGroup.Id, DateTimeOffset.UtcNow);

        var worker = BuildWorker(new FakeMessageConsumer(@event), publisher, connectionId, jobsDb);

        // Act — should not throw
        var act = async () => await worker.RunAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<(Automation, HostGroup, string connectionId, WorkflowHost host)> SeedAsync()
    {
        var connectionId = "conn-dispatch-" + Guid.NewGuid().ToString("N")[..8];
        var hostGroup = HostGroup.Create("Dispatch-Group", connectionId);
        await _platformDb.HostGroups.AddAsync(hostGroup);

        var trigger = Trigger.Create("t1", "schedule", "{}");
        var automation = Automation.Create("Dispatch Auto", null, "run-script", hostGroup.Id,
            [trigger], new TriggerConditionNode(null, "t1", null));

        typeof(Automation).GetProperty("HostGroupId")!.SetValue(automation, hostGroup.Id);
        await _platformDb.Automations.AddAsync(automation);

        var host = WorkflowHost.Create("dispatch-host", hostGroup.Id);
        host.MarkOnline();
        await _platformDb.WorkflowHosts.AddAsync(host);
        await _platformDb.SaveChangesAsync();

        return (automation, hostGroup, connectionId, host);
    }

    private TestableDispatcher BuildWorker(
        FakeMessageConsumer fakeConsumer,
        IMessagePublisher publisher,
        string connectionId,
        FlowForge.Infrastructure.Persistence.Jobs.JobsDbContext jobsDb)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_platformDb);
        services.AddScoped<IWorkflowHostRepository, WorkflowHostRepository>();
        services.AddKeyedSingleton<IJobRepository>(connectionId,
            (_, _) => new JobRepository(jobsDb));
        services.AddLogging();

        var sp = services.BuildServiceProvider();

        var handler = new JobCreatedHandler(
            publisher,
            sp,
            new RoundRobinLoadBalancer(),
            Substitute.For<IDlqWriter>(),
            NullLogger<JobCreatedHandler>.Instance);

        return new TestableDispatcher(fakeConsumer, handler);
    }

    private sealed class TestableDispatcher(
        IMessageConsumer consumer,
        JobCreatedHandler handler)
        : JobDispatcherWorker(consumer, handler)
    {
        public Task RunAsync(CancellationToken ct) => ExecuteAsync(ct);
    }
}
