using FlowForge.Contracts.Events;
using FlowForge.Domain.ValueObjects;
using FlowForge.JobAutomator.Cache;
using FlowForge.JobAutomator.Clients;
using FlowForge.JobAutomator.Initialization;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FlowForge.Integration.Tests.Workers;

public class AutomationCacheInitializerTests
{
    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GivenApiAvailable_OnStartup_SeedsCacheAndSyncsSchedules()
    {
        // Arrange
        var snapshot = MakeSnapshot();
        var apiClient = Substitute.For<IAutomationApiClient>();
        apiClient.GetSnapshotsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AutomationSnapshot> { snapshot });

        var scheduleSync = Substitute.For<IQuartzScheduleSync>();
        var cache = new AutomationCache();
        var initializer = Build(apiClient, scheduleSync, cache);

        // Act
        await initializer.StartAsync(CancellationToken.None);

        // Assert
        cache.GetAll().Should().HaveCount(1);
        cache.Get(snapshot.Id).Should().Be(snapshot);
        await scheduleSync.Received(1).SyncAsync(snapshot, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GivenApiFailsOnceThenSucceeds_SeedsCacheOnSecondAttempt()
    {
        // Arrange — first call throws, second call succeeds.
        // The base class backs off for 2 seconds after the first failure so this test
        // intentionally accepts a ~2 s runtime.
        var snapshot = MakeSnapshot();
        var apiClient = Substitute.For<IAutomationApiClient>();
        var callCount = 0;
        apiClient.GetSnapshotsAsync(Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callCount++;
                if (callCount == 1)
                    throw new HttpRequestException("API not ready yet");

                return Task.FromResult<IReadOnlyList<AutomationSnapshot>>(
                    new List<AutomationSnapshot> { snapshot });
            });

        var scheduleSync = Substitute.For<IQuartzScheduleSync>();
        var cache = new AutomationCache();
        var initializer = Build(apiClient, scheduleSync, cache);

        // Act — allow up to 10 s for the 2 s backoff + processing time
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await initializer.StartAsync(cts.Token);

        // Assert
        callCount.Should().Be(2);
        cache.GetAll().Should().HaveCount(1);
        cache.Get(snapshot.Id).Should().Be(snapshot);
    }

    [Fact]
    public async Task GivenCancellationBeforeSuccess_StopsRetryingWithoutThrowing()
    {
        // Arrange — API always fails; cancellation fires during the first backoff delay
        var apiClient = Substitute.For<IAutomationApiClient>();
        apiClient.GetSnapshotsAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("always fails"));

        var scheduleSync = Substitute.For<IQuartzScheduleSync>();
        var cache = new AutomationCache();
        var initializer = Build(apiClient, scheduleSync, cache);

        // The first backoff is 2 s; cancel after 500 ms to interrupt it
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        // Act
        var act = async () => await initializer.StartAsync(cts.Token);

        // StartAsync should return without throwing once the token is cancelled
        await act.Should().NotThrowAsync();
        cache.GetAll().Should().BeEmpty();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AutomationSnapshot MakeSnapshot() => new(
        Id: Guid.NewGuid(),
        Name: "Test",
        IsEnabled: true,
        HostGroupId: Guid.NewGuid(),
        ConnectionId: "conn-test",
        TaskId: "task-1",
        Triggers: [],
        ConditionRoot: new TriggerConditionNode(null, null, []));

    private static AutomationCacheInitializer Build(
        IAutomationApiClient apiClient,
        IQuartzScheduleSync scheduleSync,
        AutomationCache cache)
    {
        var services = new ServiceCollection();
        services.AddScoped<IAutomationApiClient>(_ => apiClient);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new AutomationCacheInitializer(scopeFactory, cache, scheduleSync,
            NullLogger<AutomationCacheInitializer>.Instance);
    }
}
