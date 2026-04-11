using FlowForge.Contracts.Events;
using FlowForge.Domain.ValueObjects;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Integration.Tests.Infrastructure;
using FlowForge.JobAutomator.Cache;
using FlowForge.JobAutomator.Handlers;
using FlowForge.JobAutomator.Initialization;
using FlowForge.JobAutomator.Workers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FlowForge.Integration.Tests.Cache;

/// <summary>
/// Pure unit tests for <see cref="AutomationCacheSyncWorker"/> — no containers required.
/// Verifies that the worker keeps the in-memory <see cref="AutomationCache"/> in sync
/// when it consumes <see cref="AutomationChangedEvent"/> messages.
/// </summary>
public class AutomationCacheSyncWorkerTests
{
    // ── Created ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenCreatedEventReceived_AddsAutomationToCache()
    {
        var snapshot = BuildSnapshot();
        var cache = new AutomationCache();

        await RunConsumerAsync(cache, new AutomationChangedEvent(
            AutomationId: snapshot.Id,
            ChangeType: ChangeType.Created,
            Automation: snapshot));

        cache.Get(snapshot.Id).Should().NotBeNull();
        cache.Get(snapshot.Id)!.Name.Should().Be(snapshot.Name);
    }

    [Fact]
    public async Task WhenCreatedEventReceivedTwice_UpsertsBothTimes()
    {
        var snapshot = BuildSnapshot();
        var cache = new AutomationCache();

        await RunConsumerAsync(cache,
            new AutomationChangedEvent(snapshot.Id, ChangeType.Created, snapshot));

        // Second creation (idempotent upsert)
        var updated = snapshot with { Name = "Updated Name" };
        await RunConsumerAsync(cache,
            new AutomationChangedEvent(snapshot.Id, ChangeType.Created, updated));

        cache.Get(snapshot.Id)!.Name.Should().Be("Updated Name");
    }

    // ── Updated ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenUpdatedEventReceived_ReplacesExistingCacheEntry()
    {
        var snapshot = BuildSnapshot();
        var cache = new AutomationCache();
        cache.Upsert(snapshot);

        var updatedSnapshot = snapshot with { IsEnabled = false };
        await RunConsumerAsync(cache, new AutomationChangedEvent(
            AutomationId: snapshot.Id,
            ChangeType: ChangeType.Updated,
            Automation: updatedSnapshot));

        cache.Get(snapshot.Id)!.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task WhenUpdatedEventReceived_ForUnknownAutomation_AddsItToCache()
    {
        var snapshot = BuildSnapshot();
        var cache = new AutomationCache(); // empty cache

        await RunConsumerAsync(cache, new AutomationChangedEvent(
            AutomationId: snapshot.Id,
            ChangeType: ChangeType.Updated,
            Automation: snapshot));

        cache.Get(snapshot.Id).Should().NotBeNull("upsert adds missing entries");
    }

    // ── Deleted ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenDeletedEventReceived_RemovesAutomationFromCache()
    {
        var snapshot = BuildSnapshot();
        var cache = new AutomationCache();
        cache.Upsert(snapshot);

        await RunConsumerAsync(cache, new AutomationChangedEvent(
            AutomationId: snapshot.Id,
            ChangeType: ChangeType.Deleted,
            Automation: null));

        cache.Get(snapshot.Id).Should().BeNull("deleted automation must be evicted");
    }

    [Fact]
    public async Task WhenDeletedEventReceived_ForUnknownAutomation_DoesNotThrow()
    {
        var cache = new AutomationCache(); // empty

        var act = async () => await RunConsumerAsync(cache, new AutomationChangedEvent(
            AutomationId: Guid.NewGuid(),
            ChangeType: ChangeType.Deleted,
            Automation: null));

        await act.Should().NotThrowAsync();
    }

    // ── Multi-event batch ─────────────────────────────────────────────────────

    [Fact]
    public async Task WhenMultipleEventsReceived_AppliesAllInOrder()
    {
        var id = Guid.NewGuid();
        var cache = new AutomationCache();

        var created  = BuildSnapshot(id, "Automation A");
        var updated  = BuildSnapshot(id, "Automation B");

        // Create then update
        await RunConsumerAsync(cache,
            new AutomationChangedEvent(id, ChangeType.Created, created),
            new AutomationChangedEvent(id, ChangeType.Updated, updated));

        cache.Get(id)!.Name.Should().Be("Automation B");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task RunConsumerAsync(AutomationCache cache, params AutomationChangedEvent[] events)
    {
        var scheduleSync = Substitute.For<IQuartzScheduleSync>();
        var handler = new AutomationChangedHandler(
            cache,
            scheduleSync,
            NullLogger<AutomationChangedHandler>.Instance);
        var consumer = new TestableConsumer(
            new FakeMessageConsumer(events.Cast<object>().ToArray()),
            handler);

        await consumer.RunAsync(CancellationToken.None);
    }

    private static AutomationSnapshot BuildSnapshot(Guid? id = null, string name = "Test Automation")
    {
        var trigger = new TriggerSnapshot(Guid.NewGuid(), "t1", "schedule", "{}");
        return new AutomationSnapshot(
            Id: id ?? Guid.NewGuid(),
            Name: name,
            IsEnabled: true,
            HostGroupId: Guid.NewGuid(),
            ConnectionId: "conn-test",
            TaskId: "run-script",
            Triggers: [trigger],
            ConditionRoot: new TriggerConditionNode(null, "t1", null));
    }

    private sealed class TestableConsumer(
        IMessageConsumer consumer,
        AutomationChangedHandler handler)
        : AutomationCacheSyncWorker(consumer, handler, NullLogger<AutomationCacheSyncWorker>.Instance)
    {
        public Task RunAsync(CancellationToken ct) => ExecuteAsync(ct);
    }}
