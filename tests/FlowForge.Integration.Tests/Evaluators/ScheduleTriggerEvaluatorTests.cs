using FlowForge.Contracts.Events;
using FlowForge.Domain.Triggers;
using FlowForge.Infrastructure.Caching;
using FlowForge.Integration.Tests.Infrastructure;
using FlowForge.JobAutomator.Evaluators;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace FlowForge.Integration.Tests.Evaluators;

[Collection("Containers")]
public class ScheduleTriggerEvaluatorTests : IAsyncLifetime
{
    private readonly SharedContainersFixture _fixture;
    private IConnectionMultiplexer _redis = null!;
    private RedisService _redisService = null!;

    public ScheduleTriggerEvaluatorTests(SharedContainersFixture fixture)
        => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _redis = await ConnectionMultiplexer.ConnectAsync(_fixture.RedisConnectionString);
        _redisService = new RedisService(_redis);
    }

    public async Task DisposeAsync()
    {
        _redis.Dispose();
        await Task.CompletedTask;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_WhenFiredFlagSet_ReturnsTrueAndDeletesFlag()
    {
        var trigger = BuildSnapshot();
        await _redisService.SetAsync($"trigger:schedule:{trigger.Id}:fired", "1");
        var sut = BuildEvaluator();

        var result = await sut.EvaluateAsync(trigger, CancellationToken.None);

        result.Should().BeTrue("the fired flag was set in Redis");
        var remaining = await _redisService.GetAsync($"trigger:schedule:{trigger.Id}:fired");
        remaining.Should().BeNull("flag must be consumed (deleted) after first evaluation");
    }

    [Fact]
    public async Task EvaluateAsync_WhenFiredFlagNotSet_ReturnsFalse()
    {
        var trigger = BuildSnapshot();
        var sut = BuildEvaluator();

        var result = await sut.EvaluateAsync(trigger, CancellationToken.None);

        result.Should().BeFalse("no fired flag exists in Redis");
    }

    [Fact]
    public async Task EvaluateAsync_CalledTwiceAfterSingleFlag_OnlyFiresOnce()
    {
        var trigger = BuildSnapshot();
        await _redisService.SetAsync($"trigger:schedule:{trigger.Id}:fired", "1");
        var sut = BuildEvaluator();

        var first = await sut.EvaluateAsync(trigger, CancellationToken.None);
        var second = await sut.EvaluateAsync(trigger, CancellationToken.None);

        first.Should().BeTrue();
        second.Should().BeFalse("the flag was already consumed on the first evaluation");
    }

    [Fact]
    public void TypeId_IsSchedule()
    {
        var sut = BuildEvaluator();
        sut.TypeId.Should().Be(TriggerTypes.Schedule);
    }

    [Fact]
    public async Task EvaluateAsync_FlagSetThenConsumed_FlagSetAgain_ReturnsTrueAgain()
    {
        var trigger = BuildSnapshot();
        var sut = BuildEvaluator();

        // First fire
        await _redisService.SetAsync($"trigger:schedule:{trigger.Id}:fired", "1");
        var first = await sut.EvaluateAsync(trigger, CancellationToken.None);

        // Flag refires (next Quartz tick)
        await _redisService.SetAsync($"trigger:schedule:{trigger.Id}:fired", "1");
        var second = await sut.EvaluateAsync(trigger, CancellationToken.None);

        first.Should().BeTrue();
        second.Should().BeTrue("a fresh fired flag triggers a second evaluation");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ScheduleTriggerEvaluator BuildEvaluator()
        => new(_redisService, NullLogger<ScheduleTriggerEvaluator>.Instance);

    private static TriggerSnapshot BuildSnapshot()
        => new(Guid.NewGuid(), "cron-daily", TriggerTypes.Schedule, "{}");
}
