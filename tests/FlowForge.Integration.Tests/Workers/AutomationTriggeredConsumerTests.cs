using FlowForge.Contracts.Events;
using FlowForge.Domain.Entities;
using FlowForge.Domain.Repositories;
using FlowForge.Domain.ValueObjects;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.DeadLetter;
using FlowForge.Infrastructure.Messaging.Outbox;
using FlowForge.Infrastructure.Persistence.Platform;
using FlowForge.Infrastructure.Repositories;
using FlowForge.Integration.Tests.Infrastructure;
using FlowForge.WebApi.Workers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowForge.Integration.Tests.Workers;

[Collection("Containers")]
public class AutomationTriggeredConsumerTests : IAsyncLifetime
{
    private readonly SharedContainersFixture _fixture;
    private PlatformDbContext _platformDb = null!;
    private IDbContextTransaction _platformTx = null!;

    public AutomationTriggeredConsumerTests(SharedContainersFixture fixture)
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
    public async Task WhenAutomationTriggered_CreatesJobAndWritesOutboxMessage()
    {
        // Arrange
        var (automation, hostGroup, connectionId) = await SeedAutomationAsync();
        using var jobsDb = _fixture.CreateJobsDbContext();
        using var jobsTx = await jobsDb.Database.BeginTransactionAsync();

        var @event = new AutomationTriggeredEvent(
            AutomationId: automation.Id,
            HostGroupId: hostGroup.Id,
            ConnectionId: connectionId,
            TaskId: automation.TaskId,
            TriggeredAt: DateTimeOffset.UtcNow);

        var consumer = BuildConsumer(new FakeMessageConsumer(@event), connectionId, jobsDb);

        // Act
        await consumer.RunAsync(CancellationToken.None);

        // Assert — job created in jobs DB
        var job = await jobsDb.Jobs.FirstOrDefaultAsync(j => j.AutomationId == automation.Id);
        job.Should().NotBeNull();
        job!.TaskId.Should().Be(automation.TaskId);
        job.ConnectionId.Should().Be(connectionId);

        // Assert — outbox message written in platform DB
        var testStart = DateTimeOffset.UtcNow.AddSeconds(-1);
        var outbox = await _platformDb.OutboxMessages
            .FirstOrDefaultAsync(m => m.EventType == "JobCreatedEvent" && m.CreatedAt >= testStart);
        outbox.Should().NotBeNull();
        outbox!.SentAt.Should().BeNull(); // not yet relayed

        // Assert — automation.ActiveJobId set
        var updatedAutomation = await _platformDb.Automations.FindAsync(automation.Id);
        updatedAutomation!.ActiveJobId.Should().Be(job.Id);

        await jobsTx.RollbackAsync();
    }

    [Fact]
    public async Task WhenActiveJobIsNonTerminal_SkipsJobCreation()
    {
        // Arrange — seed automation with an existing active non-terminal job
        var (automation, hostGroup, connectionId) = await SeedAutomationAsync();
        using var jobsDb = _fixture.CreateJobsDbContext();
        using var jobsTx = await jobsDb.Database.BeginTransactionAsync();

        var existingJob = Job.Create(automation.Id, automation.TaskId, connectionId, hostGroup.Id, DateTimeOffset.UtcNow);
        await jobsDb.Jobs.AddAsync(existingJob);
        await jobsDb.SaveChangesAsync();

        automation.SetActiveJob(existingJob.Id);
        await _platformDb.SaveChangesAsync();

        var @event = new AutomationTriggeredEvent(
            AutomationId: automation.Id,
            HostGroupId: hostGroup.Id,
            ConnectionId: connectionId,
            TaskId: automation.TaskId,
            TriggeredAt: DateTimeOffset.UtcNow);

        var consumer = BuildConsumer(new FakeMessageConsumer(@event), connectionId, jobsDb);

        // Act
        await consumer.RunAsync(CancellationToken.None);

        // Assert — no new job was created
        var jobs = await jobsDb.Jobs.Where(j => j.AutomationId == automation.Id).ToListAsync();
        jobs.Should().HaveCount(1, "duplicate job must be skipped");

        await jobsTx.RollbackAsync();
    }

    [Fact]
    public async Task WhenActiveJobIsTerminal_ClearsStaleReferenceAndCreatesNewJob()
    {
        // Arrange — seed automation with a terminal (completed) active job
        var (automation, hostGroup, connectionId) = await SeedAutomationAsync();
        using var jobsDb = _fixture.CreateJobsDbContext();
        using var jobsTx = await jobsDb.Database.BeginTransactionAsync();

        var completedJob = Job.Create(automation.Id, automation.TaskId, connectionId, hostGroup.Id, DateTimeOffset.UtcNow);
        completedJob.Transition(FlowForge.Domain.Enums.JobStatus.Started);
        completedJob.Transition(FlowForge.Domain.Enums.JobStatus.InProgress);
        completedJob.Transition(FlowForge.Domain.Enums.JobStatus.Completed);
        await jobsDb.Jobs.AddAsync(completedJob);
        await jobsDb.SaveChangesAsync();

        automation.SetActiveJob(completedJob.Id);
        await _platformDb.SaveChangesAsync();

        var @event = new AutomationTriggeredEvent(
            AutomationId: automation.Id,
            HostGroupId: hostGroup.Id,
            ConnectionId: connectionId,
            TaskId: automation.TaskId,
            TriggeredAt: DateTimeOffset.UtcNow);

        var consumer = BuildConsumer(new FakeMessageConsumer(@event), connectionId, jobsDb);

        // Act
        await consumer.RunAsync(CancellationToken.None);

        // Assert — a new job was created
        var jobs = await jobsDb.Jobs.Where(j => j.AutomationId == automation.Id).ToListAsync();
        jobs.Should().HaveCount(2, "new job should be created after stale reference is cleared");

        await jobsTx.RollbackAsync();
    }

    [Fact]
    public async Task WhenAutomationIsDisabled_DropsEventAndCreatesNoJob()
    {
        // Arrange — seed disabled automation
        var (automation, hostGroup, connectionId) = await SeedAutomationAsync();
        automation.Disable();
        await _platformDb.SaveChangesAsync();

        using var jobsDb = _fixture.CreateJobsDbContext();
        using var jobsTx = await jobsDb.Database.BeginTransactionAsync();

        var @event = new AutomationTriggeredEvent(
            AutomationId: automation.Id,
            HostGroupId: hostGroup.Id,
            ConnectionId: connectionId,
            TaskId: automation.TaskId,
            TriggeredAt: DateTimeOffset.UtcNow);

        var consumer = BuildConsumer(new FakeMessageConsumer(@event), connectionId, jobsDb);

        // Act
        await consumer.RunAsync(CancellationToken.None);

        // Assert — no job created
        var jobs = await jobsDb.Jobs.Where(j => j.AutomationId == automation.Id).ToListAsync();
        jobs.Should().BeEmpty("disabled automation must not produce jobs");

        // Assert — no outbox message written
        var testStart = DateTimeOffset.UtcNow.AddSeconds(-1);
        var outbox = await _platformDb.OutboxMessages
            .FirstOrDefaultAsync(m => m.EventType == "JobCreatedEvent" && m.CreatedAt >= testStart);
        outbox.Should().BeNull();

        await jobsTx.RollbackAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<(Automation automation, HostGroup hostGroup, string connectionId)> SeedAutomationAsync()
    {
        var connectionId = "conn-" + Guid.NewGuid().ToString("N")[..8];
        var trigger = Trigger.Create("t1", "schedule", "{}");
        var automation = Automation.Create("Test Auto", null, "task-1", Guid.Empty, [trigger],
            new TriggerConditionNode(null, "t1", null));

        var hostGroup = HostGroup.Create("Test Group", connectionId);

        // Fix the HostGroupId on the automation via reflection (factory always creates new Guid)
        typeof(Automation).GetProperty("HostGroupId")!.SetValue(automation, hostGroup.Id);

        await _platformDb.HostGroups.AddAsync(hostGroup);
        await _platformDb.Automations.AddAsync(automation);
        await _platformDb.SaveChangesAsync();

        return (automation, hostGroup, connectionId);
    }

    private TestableConsumer BuildConsumer(FakeMessageConsumer fakeConsumer, string connectionId,
        FlowForge.Infrastructure.Persistence.Jobs.JobsDbContext jobsDb)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_platformDb);
        services.AddScoped<IAutomationRepository, AutomationRepository>();
        services.AddScoped<IHostGroupRepository, HostGroupRepository>();
        services.AddScoped<IOutboxWriter, OutboxWriter>();
        services.AddKeyedSingleton<IJobRepository>(connectionId,
            (_, _) => new JobRepository(jobsDb));
        services.AddSingleton<IMessageConsumer>(fakeConsumer);
        services.AddLogging();

        var sp = services.BuildServiceProvider();
        return new TestableConsumer(fakeConsumer, sp, NullLogger<AutomationTriggeredConsumer>.Instance);
    }

    // Exposes the protected ExecuteAsync for testing
    private sealed class TestableConsumer(
        IMessageConsumer consumer,
        IServiceProvider sp,
        Microsoft.Extensions.Logging.ILogger<AutomationTriggeredConsumer> logger)
        : AutomationTriggeredConsumer(consumer, sp.GetRequiredService<IServiceScopeFactory>(), NSubstitute.Substitute.For<IDlqWriter>(), logger)
    {
        public Task RunAsync(CancellationToken ct) => ExecuteAsync(ct);
    }
}
