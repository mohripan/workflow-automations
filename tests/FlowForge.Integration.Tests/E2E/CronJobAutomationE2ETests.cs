using FlowForge.Contracts.Events;
using FlowForge.Domain.Entities;
using FlowForge.Domain.Enums;
using FlowForge.Domain.Repositories;
using FlowForge.Domain.ValueObjects;
using FlowForge.Infrastructure.Caching;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.DeadLetter;
using FlowForge.Infrastructure.Messaging.Outbox;
using FlowForge.Infrastructure.Messaging.Redis;
using FlowForge.Infrastructure.Persistence.Platform;
using FlowForge.Infrastructure.Repositories;
using FlowForge.Integration.Tests.Infrastructure;
using FlowForge.JobAutomator.Cache;
using FlowForge.JobAutomator.Evaluators;
using FlowForge.JobAutomator.Options;
using FlowForge.JobAutomator.Workers;
using FlowForge.JobOrchestrator.LoadBalancing;
using FlowForge.JobOrchestrator.Workers;
using FlowForge.WebApi.DTOs.Responses;
using FlowForge.WebApi.Hubs;
using FlowForge.WebApi.Options;
using FlowForge.WebApi.Workers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FlowForge.Integration.Tests.E2E;

/// <summary>
/// End-to-end test that covers the full automation lifecycle:
///
///   1. Create an automation with a cron (schedule) trigger whose workflow task is
///      <c>run-script</c> pointing at <c>examples/send-email-resend/send_email.py</c>.
///   2. Simulate the Quartz.NET schedule firing via the Redis flag mechanism.
///   3. Run the full pipeline: AutomationWorker → AutomationTriggeredConsumer
///      → OutboxRelayWorker → JobDispatcherWorker.
///   4. Spawn the real <c>FlowForge.WorkflowEngine</c> process so it executes
///      <c>send_email.py</c> (requires <c>RESEND_API_KEY</c> in the environment;
///      falls back to a no-op script when the key is absent so CI still passes).
///   5. Consume the <c>JobStatusChangedEvent</c> events published by the engine.
///   6. Delete the automation and verify cleanup.
/// </summary>
[Collection("Containers")]
public class CronJobAutomationE2ETests : IAsyncLifetime
{
    private readonly SharedContainersFixture _fixture;
    private PlatformDbContext _platformDb = null!;
    private IConnectionMultiplexer _redis = null!;
    private RedisService _redisService = null!;

    public CronJobAutomationE2ETests(SharedContainersFixture fixture)
        => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _platformDb = _fixture.CreatePlatformDbContext();
        _redis = await ConnectionMultiplexer.ConnectAsync(_fixture.RedisConnectionString);
        _redisService = new RedisService(_redis);
    }

    public async Task DisposeAsync()
    {
        _redis.Dispose();
        await _platformDb.DisposeAsync();
    }

    // ── Main E2E test ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ScheduleTrigger_WithSendEmailWorkflow_FullLifecycleAndDeletesAutomation()
    {
        // ── PHASE 0 — Determine script + build configuration ─────────────────

        var repoRoot    = FindRepoRoot();
        var pythonPath  = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "python" : "python3";
        var resendKey   = Environment.GetEnvironmentVariable("RESEND_API_KEY");
        var engineDll   = EnsureWorkflowEngineBuilt(repoRoot);

        // If RESEND_API_KEY is set use the real send_email.py; otherwise fall
        // back to a simple no-op script so the test passes in CI without secrets.
        string scriptPath;
        string[] packages;
        if (!string.IsNullOrEmpty(resendKey))
        {
            scriptPath = Path.Combine(repoRoot, "examples", "send-email-resend", "send_email.py");
            packages   = ["resend"];
        }
        else
        {
            scriptPath = Path.Combine(Path.GetTempPath(), $"flowforge-e2e-{Guid.NewGuid():N}.py");
            await File.WriteAllTextAsync(scriptPath,
                "print('E2E workflow ran successfully (no email — RESEND_API_KEY not set)')");
            packages = [];
        }

        // TaskConfig tells RunScriptHandler which interpreter, script, and packages to use.
        var taskConfig = JsonSerializer.Serialize(new
        {
            interpreter = pythonPath,
            scriptPath  = scriptPath.Replace('\\', '/'),
            packages
        });

        // ── PHASE 1 — Seed database ───────────────────────────────────────────

        var connectionId = "conn-e2e-" + Guid.NewGuid().ToString("N")[..8];
        var hostGroup = HostGroup.Create("E2E-Group", connectionId);
        await _platformDb.HostGroups.AddAsync(hostGroup);

        var trigger = Trigger.Create("cron-daily", "schedule", """{"expression":"0 0 * * *"}""");
        var automation = Automation.Create(
            "E2E Cron Automation",
            "Created and deleted by E2E integration test",
            "run-script",
            hostGroup.Id,
            [trigger],
            new TriggerConditionNode(null, "cron-daily", null),
            taskConfig: taskConfig);

        // Factory sets HostGroupId to a new Guid; pin it to our seeded host group.
        typeof(Automation).GetProperty("HostGroupId")!.SetValue(automation, hostGroup.Id);
        await _platformDb.Automations.AddAsync(automation);

        var host = WorkflowHost.Create("e2e-host", hostGroup.Id);
        host.MarkOnline();
        await _platformDb.WorkflowHosts.AddAsync(host);
        await _platformDb.SaveChangesAsync();

        using var jobsDb = _fixture.CreateJobsDbContext();

        // ── PHASE 2 — AutomationWorker: schedule fires, event published ───────

        var cache = new AutomationCache();
        cache.Upsert(new AutomationSnapshot(
            automation.Id,
            automation.Name,
            IsEnabled: true,
            hostGroup.Id,
            connectionId,
            automation.TaskId,
            [new TriggerSnapshot(trigger.Id, trigger.Name, trigger.TypeId, trigger.ConfigJson)],
            automation.ConditionRoot,
            TaskConfig: taskConfig));

        // Set the Redis flag that Quartz.NET would set when the cron fires.
        await _redisService.SetAsync($"trigger:schedule:{trigger.Id}:fired", "1");

        AutomationTriggeredEvent? triggeredEvent = null;
        var automatorPublisher = Substitute.For<IMessagePublisher>();
        automatorPublisher
            .PublishAsync(Arg.Any<AutomationTriggeredEvent>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                triggeredEvent = ci.ArgAt<AutomationTriggeredEvent>(0);
                return Task.CompletedTask;
            });

        var worker = new AutomationWorker(
            cache,
            [new ScheduleTriggerEvaluator(_redisService, NullLogger<ScheduleTriggerEvaluator>.Instance)],
            new TriggerConditionEvaluator(NullLogger<TriggerConditionEvaluator>.Instance),
            automatorPublisher,
            _redisService,
            Options.Create(new AutomationWorkerOptions { EvaluationIntervalSeconds = 0 }),
            NullLogger<AutomationWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntil(() => triggeredEvent != null, TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        triggeredEvent.Should().NotBeNull("schedule trigger fired — event must be published");
        triggeredEvent!.AutomationId.Should().Be(automation.Id);
        triggeredEvent.TaskConfig.Should().Be(taskConfig, "task config must flow through the event");

        (await _redisService.GetAsync($"trigger:schedule:{trigger.Id}:fired"))
            .Should().BeNull("fired flag must be consumed by the evaluator");

        // ── PHASE 3 — AutomationTriggeredConsumer: job created ────────────────

        var triggeredConsumer = BuildTriggeredConsumer(
            new FakeMessageConsumer(triggeredEvent), connectionId, jobsDb);
        await triggeredConsumer.RunAsync(CancellationToken.None);

        var job = await jobsDb.Jobs.FirstOrDefaultAsync(j => j.AutomationId == automation.Id);
        job.Should().NotBeNull("job must be created after automation trigger");
        job!.Status.Should().Be(JobStatus.Pending);
        job.TaskConfig.Should().Be(taskConfig, "task config must be stored on the job");

        var outboxMsg = await _platformDb.OutboxMessages
            .FirstOrDefaultAsync(m => m.EventType == "JobCreatedEvent");
        outboxMsg.Should().NotBeNull("JobCreatedEvent must be staged in the outbox");

        await _platformDb.Entry(automation).ReloadAsync();
        automation.ActiveJobId.Should().Be(job.Id);

        // ── PHASE 4 — OutboxRelayWorker: JobCreatedEvent → Redis ─────────────

        // Clear any pre-existing entries on the stream to keep assertions clean.
        try { await _redis.GetDatabase().KeyDeleteAsync(StreamNames.JobCreated); } catch { /* ignore */ }
        try { await _redis.GetDatabase().KeyDeleteAsync(StreamNames.JobStatusChanged); } catch { /* ignore */ }

        var relayWorker = BuildOutboxRelayWorker();
        using var relayCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await relayWorker.StartAsync(relayCts.Token);
        await Task.Delay(700);
        await relayWorker.StopAsync(CancellationToken.None);

        (await _redis.GetDatabase().StreamReadAsync(StreamNames.JobCreated, "0-0"))
            .Should().NotBeEmpty("JobCreatedEvent must be published to the Redis stream");

        // ── PHASE 5 — JobDispatcherWorker: job → Started + host assigned ──────

        var jobCreatedEvent = new JobCreatedEvent(
            job.Id, connectionId, automation.Id, hostGroup.Id, DateTimeOffset.UtcNow);

        JobAssignedEvent? assignedEvent = null;
        var dispatchPublisher = Substitute.For<IMessagePublisher>();
        dispatchPublisher
            .PublishAsync(Arg.Any<JobAssignedEvent>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                assignedEvent = ci.ArgAt<JobAssignedEvent>(0);
                return Task.CompletedTask;
            });

        var dispatcher = BuildDispatcher(
            new FakeMessageConsumer(jobCreatedEvent), dispatchPublisher, connectionId, jobsDb);
        await dispatcher.RunAsync(CancellationToken.None);

        var startedJob = await jobsDb.Jobs.FindAsync(job.Id);
        startedJob!.Status.Should().Be(JobStatus.Started);
        assignedEvent.Should().NotBeNull("job must be assigned to a host");

        // ── PHASE 6 — WorkflowEngine process: runs send_email.py ─────────────

        await SpawnWorkflowEngineAsync(
            engineDll, job.Id, automation.Id, connectionId, resendKey, timeout: TimeSpan.FromSeconds(60));

        // ── PHASE 7 — Consume JobStatusChanged events published by engine ─────

        // The engine publishes InProgress then Completed (or Error) events to Redis.
        // We read them in order and drive them through the consumer to update the DB.
        var statusEvents = await ReadJobStatusEventsAsync(job.Id, expectedCount: 2, TimeSpan.FromSeconds(10));
        statusEvents.Should().HaveCountGreaterOrEqualTo(1, "engine must publish at least one status event");

        foreach (var statusEvent in statusEvents)
        {
            await RunStatusChangedConsumerAsync(
                new FakeMessageConsumer(statusEvent), connectionId, jobsDb);
        }

        // ── PHASE 8 — Verify job completed + ActiveJobId cleared ──────────────

        var completedJob = await jobsDb.Jobs.FindAsync(job.Id);
        completedJob!.Status.Should().BeOneOf(
            JobStatus.Completed, JobStatus.CompletedUnsuccessfully);

        await _platformDb.Entry(automation).ReloadAsync();
        automation.ActiveJobId.Should().BeNull(
            "ActiveJobId must be cleared when job reaches terminal status");

        // ── PHASE 9 — Delete automation and verify cleanup ────────────────────

        cache.Remove(automation.Id);
        _platformDb.Automations.Remove(automation);
        _platformDb.HostGroups.Remove(hostGroup);

        // Remove all outbox messages so subsequent tests in the same collection
        // do not find stale committed rows when querying by event type.
        var outboxToClean = await _platformDb.OutboxMessages.ToListAsync();
        _platformDb.OutboxMessages.RemoveRange(outboxToClean);

        await _platformDb.SaveChangesAsync();

        (await _platformDb.Automations.FindAsync(automation.Id))
            .Should().BeNull("automation must be deleted after E2E test");
        cache.Get(automation.Id)
            .Should().BeNull("automation must be evicted from the in-memory cache");
    }

    // ── WorkflowEngine process helpers ───────────────────────────────────────

    /// <summary>
    /// Ensures the WorkflowEngine DLL exists (builds it if not), returning its path.
    /// </summary>
    private static string EnsureWorkflowEngineBuilt(string repoRoot)
    {
        var assembly = typeof(CronJobAutomationE2ETests).Assembly.Location;
        var buildConfig = assembly.Contains("Release", StringComparison.OrdinalIgnoreCase)
            ? "Release" : "Debug";
        var dllPath = Path.Combine(
            repoRoot,
            $"src/services/FlowForge.WorkflowEngine/bin/{buildConfig}/net10.0/FlowForge.WorkflowEngine.dll");

        if (File.Exists(dllPath))
            return dllPath;

        // Binary not found — build now.
        var projectPath = Path.Combine(
            repoRoot, "src/services/FlowForge.WorkflowEngine/FlowForge.WorkflowEngine.csproj");

        using var build = Process.Start(new ProcessStartInfo("dotnet",
            $"build \"{projectPath}\" --configuration {buildConfig} -v quiet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false
        })!;

        build.WaitForExit(TimeSpan.FromMinutes(2));
        if (build.ExitCode != 0)
            throw new InvalidOperationException(
                $"WorkflowEngine build failed (exit {build.ExitCode}):\n{build.StandardError.ReadToEnd()}");

        if (!File.Exists(dllPath))
            throw new FileNotFoundException($"WorkflowEngine DLL not found after build: {dllPath}");

        return dllPath;
    }

    /// <summary>
    /// Spawns <c>FlowForge.WorkflowEngine</c> as a child process with Testcontainers
    /// connection strings injected as environment variables.
    /// </summary>
    private async Task SpawnWorkflowEngineAsync(
        string engineDll,
        Guid jobId,
        Guid automationId,
        string connectionId,
        string? resendApiKey,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo("dotnet", $"\"{engineDll}\"")
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        // Connection strings — point the engine at the Testcontainers instances.
        startInfo.Environment["JOB_ID"]             = jobId.ToString();
        startInfo.Environment["JOB_AUTOMATION_ID"]  = automationId.ToString();
        startInfo.Environment["CONNECTION_ID"]       = connectionId;

        startInfo.Environment["Redis__ConnectionString"]             = _fixture.RedisConnectionString;
        startInfo.Environment["ConnectionStrings__DefaultConnection"] = _fixture.PlatformConnectionString;
        startInfo.Environment[$"JobConnections__{connectionId}__ConnectionString"] =
            _fixture.JobsConnectionString;

        // Use the same test encryption key as the rest of the suite.
        startInfo.Environment["FlowForge__EncryptionKey"] =
            "dGVzdGtleXRlc3RrZXl0ZXN0a2V5dGVzdGtleXRlc3Q=";

        // Pass RESEND_API_KEY through if present (required for send_email.py).
        if (!string.IsNullOrEmpty(resendApiKey))
            startInfo.Environment["RESEND_API_KEY"] = resendApiKey;

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Capture output for debugging in case of failure.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var completed = await process.WaitForExitAsync().WaitAsync(timeout)
            .ContinueWith(t => t.IsCompletedSuccessfully);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (!completed)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"WorkflowEngine timed out after {timeout.TotalSeconds}s.\nStdout: {stdout}\nStderr: {stderr}");
        }

        // Exit code 1 = Error; 0 = Completed or Cancelled.
        // We allow both — the consumer will check the actual job status.
        // Fail loudly on unexpected exit codes (negative = unhandled crash, >1 = unknown).
        if (process.ExitCode is not (0 or 1))
            throw new Exception(
                $"WorkflowEngine exited with unexpected code {process.ExitCode}.\nStdout: {stdout}\nStderr: {stderr}");
    }

    /// <summary>
    /// Reads <see cref="JobStatusChangedEvent"/> entries from the Redis stream,
    /// filtered to <paramref name="jobId"/>, waiting until at least
    /// <paramref name="expectedCount"/> arrive or the timeout elapses.
    /// </summary>
    private async Task<List<JobStatusChangedEvent>> ReadJobStatusEventsAsync(
        Guid jobId, int expectedCount, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var redisDb  = _redis.GetDatabase();

        while (DateTime.UtcNow < deadline)
        {
            var entries = await redisDb.StreamReadAsync(StreamNames.JobStatusChanged, "0-0");
            var events = entries
                .Select(e =>
                {
                    var payload = (string?)e.Values.FirstOrDefault(v => v.Name == "payload").Value;
                    return payload is null ? null
                        : JsonSerializer.Deserialize<JobStatusChangedEvent>(payload);
                })
                .OfType<JobStatusChangedEvent>()
                .Where(e => e.JobId == jobId)
                .OrderBy(e => e.UpdatedAt)
                .ToList();

            if (events.Count >= expectedCount)
                return events;

            await Task.Delay(150);
        }

        // Return whatever we have — caller will assert.
        var final = await redisDb.StreamReadAsync(StreamNames.JobStatusChanged, "0-0");
        return final
            .Select(e =>
            {
                var payload = (string?)e.Values.FirstOrDefault(v => v.Name == "payload").Value;
                return payload is null ? null
                    : JsonSerializer.Deserialize<JobStatusChangedEvent>(payload);
            })
            .OfType<JobStatusChangedEvent>()
            .Where(e => e.JobId == jobId)
            .OrderBy(e => e.UpdatedAt)
            .ToList();
    }

    // ── Repo-root discovery ───────────────────────────────────────────────────

    private static string FindRepoRoot()
    {
        var dir = Path.GetDirectoryName(typeof(CronJobAutomationE2ETests).Assembly.Location)!;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "FlowForge.sln")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException(
            "Could not find FlowForge.sln — repository root not located");
    }

    // ── Helper builders (same patterns as existing tests) ────────────────────

    private TestableTriggeredConsumer BuildTriggeredConsumer(
        FakeMessageConsumer fakeConsumer,
        string connectionId,
        FlowForge.Infrastructure.Persistence.Jobs.JobsDbContext jobsDb)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_platformDb);
        services.AddScoped<IAutomationRepository, AutomationRepository>();
        services.AddScoped<IHostGroupRepository, HostGroupRepository>();
        services.AddScoped<IOutboxWriter, OutboxWriter>();
        services.AddKeyedSingleton<IJobRepository>(connectionId,
            (_, _) => new JobRepository(jobsDb));
        services.AddLogging();

        var sp = services.BuildServiceProvider();
        return new TestableTriggeredConsumer(
            fakeConsumer, sp, NullLogger<AutomationTriggeredConsumer>.Instance);
    }

    private OutboxRelayWorker BuildOutboxRelayWorker()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_platformDb);
        services.AddLogging();
        var sp = services.BuildServiceProvider();

        return new OutboxRelayWorker(
            _redis,
            sp.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new OutboxRelayOptions()),
            NullLogger<OutboxRelayWorker>.Instance);
    }

    private TestableDispatcher BuildDispatcher(
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
        return new TestableDispatcher(
            fakeConsumer, publisher, sp,
            new RoundRobinLoadBalancer(),
            Substitute.For<IDlqWriter>(),
            NullLogger<JobDispatcherWorker>.Instance);
    }

    private async Task RunStatusChangedConsumerAsync(
        FakeMessageConsumer fakeConsumer,
        string connectionId,
        FlowForge.Infrastructure.Persistence.Jobs.JobsDbContext jobsDb)
    {
        var hubContext = Substitute.For<IHubContext<JobStatusHub, IJobStatusClient>>();
        var clients    = Substitute.For<IHubClients<IJobStatusClient>>();
        var client     = Substitute.For<IJobStatusClient>();
        hubContext.Clients.Returns(clients);
        clients.Group(Arg.Any<string>()).Returns(client);

        var services = new ServiceCollection();
        services.AddSingleton(_platformDb);
        services.AddScoped<IAutomationRepository, AutomationRepository>();
        services.AddScoped<IOutboxWriter, OutboxWriter>();
        services.AddKeyedSingleton<IJobRepository>(connectionId,
            (_, _) => new JobRepository(jobsDb));
        services.AddLogging();

        var sp = services.BuildServiceProvider();
        var consumer = new TestableStatusConsumer(
            fakeConsumer, sp, hubContext, NullLogger<JobStatusChangedConsumer>.Instance);

        await consumer.RunAsync(CancellationToken.None);
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }

    // ── Testable BackgroundService wrappers ───────────────────────────────────

    private sealed class TestableTriggeredConsumer(
        IMessageConsumer consumer, IServiceProvider sp,
        Microsoft.Extensions.Logging.ILogger<AutomationTriggeredConsumer> logger)
        : AutomationTriggeredConsumer(
            consumer, sp.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<IDlqWriter>(), logger)
    {
        public Task RunAsync(CancellationToken ct) => ExecuteAsync(ct);
    }

    private sealed class TestableDispatcher(
        IMessageConsumer consumer, IMessagePublisher publisher,
        IServiceProvider sp, ILoadBalancer loadBalancer,
        IDlqWriter dlqWriter, Microsoft.Extensions.Logging.ILogger<JobDispatcherWorker> logger)
        : JobDispatcherWorker(consumer, publisher, sp, loadBalancer, dlqWriter, logger)
    {
        public Task RunAsync(CancellationToken ct) => ExecuteAsync(ct);
    }

    private sealed class TestableStatusConsumer(
        IMessageConsumer consumer, IServiceProvider sp,
        IHubContext<JobStatusHub, IJobStatusClient> hubContext,
        Microsoft.Extensions.Logging.ILogger<JobStatusChangedConsumer> logger)
        : JobStatusChangedConsumer(
            consumer, sp.GetRequiredService<IServiceScopeFactory>(),
            hubContext, Substitute.For<IDlqWriter>(), logger)
    {
        public Task RunAsync(CancellationToken ct) => ExecuteAsync(ct);
    }
}
