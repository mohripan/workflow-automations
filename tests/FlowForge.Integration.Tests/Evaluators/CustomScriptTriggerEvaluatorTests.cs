using FlowForge.Contracts.Events;
using FlowForge.Domain.Triggers;
using FlowForge.Infrastructure.Caching;
using FlowForge.JobAutomator.Evaluators;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FlowForge.Integration.Tests.Evaluators;

/// <summary>
/// Tests for <see cref="CustomScriptTriggerEvaluator"/> — exercises real Python process spawning.
/// Requires Python 3 on PATH (python3 on Linux/macOS, python on Windows).
/// </summary>
public class CustomScriptTriggerEvaluatorTests
{
    private static readonly string PythonPath =
        Environment.GetEnvironmentVariable("PYTHON_PATH")
        ?? (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "python" : "python3");

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_ScriptPrintsTrueAndExitsZero_ReturnsTrue()
    {
        var redis = BuildFakeRedis();
        var sut = BuildEvaluator(redis);
        var trigger = BuildSnapshot(scriptContent: "print('true')");

        var result = await sut.EvaluateAsync(trigger, CancellationToken.None);

        result.Should().BeTrue("script printed 'true' with exit 0");
    }

    [Fact]
    public async Task EvaluateAsync_ScriptPrintsFalseAndExitsZero_ReturnsFalse()
    {
        var redis = BuildFakeRedis();
        var sut = BuildEvaluator(redis);
        var trigger = BuildSnapshot(scriptContent: "print('false')");

        var result = await sut.EvaluateAsync(trigger, CancellationToken.None);

        result.Should().BeFalse("script printed 'false'");
    }

    [Fact]
    public async Task EvaluateAsync_ScriptExitsWithNonZero_ReturnsFalse()
    {
        var redis = BuildFakeRedis();
        var sut = BuildEvaluator(redis);
        var trigger = BuildSnapshot(scriptContent: "import sys; sys.exit(1)");

        var result = await sut.EvaluateAsync(trigger, CancellationToken.None);

        result.Should().BeFalse("non-zero exit code means the trigger did not fire");
    }

    [Fact]
    public async Task EvaluateAsync_ScriptThrowsException_ReturnsFalse()
    {
        var redis = BuildFakeRedis();
        var sut = BuildEvaluator(redis);
        var trigger = BuildSnapshot(scriptContent: "raise RuntimeError('boom')");

        var result = await sut.EvaluateAsync(trigger, CancellationToken.None);

        result.Should().BeFalse("a script that throws should be treated as not-fired");
    }

    [Fact]
    public async Task EvaluateAsync_WhenWithinPollingInterval_ReturnsFalseWithoutRunning()
    {
        // Simulate a last-run timestamp that is recent (within the 30s polling interval)
        var storedKeys = new Dictionary<string, string>();
        var redis = Substitute.For<IRedisService>();
        redis.GetAsync(Arg.Any<string>())
            .Returns(ci => Task.FromResult<string?>(
                storedKeys.TryGetValue(ci.Arg<string>(), out var v) ? v : null));
        redis.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns(ci => { storedKeys[ci.ArgAt<string>(0)] = ci.ArgAt<string>(1); return Task.CompletedTask; });

        var trigger = BuildSnapshot(scriptContent: "print('true')", pollingIntervalSeconds: 30);

        // Simulate script was run 5 seconds ago
        storedKeys[$"trigger:custom-script:{trigger.Id}:last-run"] =
            DateTimeOffset.UtcNow.AddSeconds(-5).ToString("O");

        var sut = BuildEvaluator(redis);

        var result = await sut.EvaluateAsync(trigger, CancellationToken.None);

        result.Should().BeFalse("we are still within the polling interval");
    }

    [Fact]
    public void TypeId_IsCustomScript()
    {
        var sut = BuildEvaluator(Substitute.For<IRedisService>());
        sut.TypeId.Should().Be(TriggerTypes.CustomScript);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private CustomScriptTriggerEvaluator BuildEvaluator(IRedisService redis)
    {
        var options = Options.Create(new CustomScriptOptions
        {
            PythonPath = PythonPath,
            ScriptTempDir = Path.Combine(Path.GetTempPath(), "flowforge-tests", Guid.NewGuid().ToString("N")),
            VenvCacheDir  = Path.Combine(Path.GetTempPath(), "flowforge-venvs")
        });
        return new CustomScriptTriggerEvaluator(redis, options, NullLogger<CustomScriptTriggerEvaluator>.Instance);
    }

    /// <summary>
    /// Fake Redis that records SetAsync calls and returns stored values via GetAsync.
    /// The polling-interval key starts absent so the evaluator always runs on first call.
    /// </summary>
    private static IRedisService BuildFakeRedis()
    {
        var stored = new Dictionary<string, string>();
        var redis = Substitute.For<IRedisService>();
        redis.GetAsync(Arg.Any<string>())
            .Returns(ci => Task.FromResult<string?>(
                stored.TryGetValue(ci.Arg<string>(), out var v) ? v : null));
        redis.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns(ci => { stored[ci.ArgAt<string>(0)] = ci.ArgAt<string>(1); return Task.CompletedTask; });
        redis.DeleteAsync(Arg.Any<string>())
            .Returns(ci => { stored.Remove(ci.Arg<string>()); return Task.CompletedTask; });
        return redis;
    }

    private static TriggerSnapshot BuildSnapshot(
        string scriptContent,
        int pollingIntervalSeconds = 30,
        int timeoutSeconds = 10)
    {
        var config = JsonSerializer.Serialize(new
        {
            ScriptContent = scriptContent,
            PollingIntervalSeconds = pollingIntervalSeconds,
            TimeoutSeconds = timeoutSeconds
        });
        return new TriggerSnapshot(Guid.NewGuid(), "py-trigger", TriggerTypes.CustomScript, config);
    }
}
