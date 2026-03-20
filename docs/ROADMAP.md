# ROADMAP.md — FlowForge Planned Improvements

This document tracks planned improvements to FlowForge. Each item includes the motivation, the affected components, and the design decisions to follow during implementation. Items are ordered by implementation priority.

---

## Status Legend

| Symbol | Meaning |
|---|---|
| `[ ]` | Not started |
| `[~]` | In progress |
| `[x]` | Done |

---

## Items

| # | Title | Area | Status |
|---|---|---|---|
| 1 | [Unit & Integration Test Suite](#1-unit--integration-test-suite) | Quality | `[x]` |
| 2 | [Health Checks & Kubernetes Probes](#2-health-checks--kubernetes-probes) | Reliability | `[x]` |
| 3 | [OpenTelemetry Metrics](#3-opentelemetry-metrics) | Observability | `[x]` |
| 4 | [Job Timeout Enforcement](#4-job-timeout-enforcement) | Reliability | `[x]` |
| 5 | [Dead Letter Queue for Poison Messages](#5-dead-letter-queue-for-poison-messages) | Reliability | `[x]` |
| 6 | [Job Auto-Retry on Failure](#6-job-auto-retry-on-failure) | Business Logic | `[x]` |
| 7 | [Configurable Polling Intervals via IOptions](#7-configurable-polling-intervals-via-ioptions) | Operability | `[ ]` |

---

## 1. Unit & Integration Test Suite

### Problem

There are no test projects in the solution. All business logic — trigger condition evaluation, job state machine transitions, domain invariant enforcement, worker resilience — is exercised only through manual testing or in production. Every refactor carries risk of silent regression.

This was explicitly deferred until all features were in place. All roadmap items are now complete, which makes this the right time to add tests.

### Scope

Two test projects:

**`FlowForge.Domain.Tests`** (pure unit tests, no infrastructure):
- Trigger condition tree evaluation (`TriggerConditionEvaluator.Evaluate`) — AND, OR, nested combinations, empty conditions, missing trigger names
- Job state machine transitions — valid paths, invalid transitions throw `InvalidJobTransitionException`
- Automation domain invariants — null condition root rejected, empty trigger list rejected, condition references unknown trigger name rejected
- `IsTerminal()` extension — all terminal and non-terminal statuses
- `ActiveJobId` lifecycle — `SetActiveJob`, `ClearActiveJob`, duplicate prevention logic

**`FlowForge.Integration.Tests`** (requires TestContainers — PostgreSQL + Redis):
- `AutomationTriggeredConsumer` — full flow: receive event → check duplicate → create job → write outbox
- `JobStatusChangedConsumer` — terminal status clears `ActiveJobId`; non-terminal does not
- `OutboxRelayWorker` — unsent messages are published to Redis and marked sent
- `AutomationCacheInitializer` — retries when API is unavailable; succeeds on nth attempt; stops retrying on cancellation
- `AutomationService` — webhook secret validation (valid secret passes, invalid rejects, no secret allows when no hash stored)

### Design

```
tests/
├── FlowForge.Domain.Tests/
│   ├── FlowForge.Domain.Tests.csproj
│   ├── TriggerConditionEvaluatorTests.cs
│   ├── JobStateMachineTests.cs
│   └── AutomationInvariantsTests.cs
└── FlowForge.Integration.Tests/
    ├── FlowForge.Integration.Tests.csproj
    ├── Infrastructure/        ← shared test fixtures (containers, DbContext setup)
    ├── Workers/
    │   ├── AutomationTriggeredConsumerTests.cs
    │   ├── JobStatusChangedConsumerTests.cs
    │   └── OutboxRelayWorkerTests.cs
    └── Services/
        └── AutomationServiceTests.cs
```

**NuGet packages:**
- `xunit` + `xunit.runner.visualstudio`
- `FluentAssertions` — readable assertion syntax
- `NSubstitute` — lightweight mock/stub for unit tests
- `Testcontainers.PostgreSql` + `Testcontainers.Redis` — ephemeral containers for integration tests
- `Microsoft.EntityFrameworkCore.InMemory` — only for cases where Testcontainers is overkill

**Conventions:**
- Unit test class names match the class under test: `TriggerConditionEvaluatorTests`
- Integration tests use a shared `IAsyncLifetime` fixture that starts containers once per collection
- Integration tests roll back DB changes after each test using `IDbContextTransaction` that is never committed
- No mocking the database in integration tests — real PostgreSQL via Testcontainers only

### Files to Create

- `tests/FlowForge.Domain.Tests/FlowForge.Domain.Tests.csproj`
- `tests/FlowForge.Domain.Tests/**/*.cs`
- `tests/FlowForge.Integration.Tests/FlowForge.Integration.Tests.csproj`
- `tests/FlowForge.Integration.Tests/**/*.cs`
- `FlowForge.sln` — add new test projects

---

## 2. Health Checks & Kubernetes Probes

### Problem

No service exposes `/health/live` or `/health/ready` endpoints. Kubernetes has no way to determine whether a pod is actually ready to serve traffic or has hit an unrecoverable error. This causes two production failure modes:

1. **Premature routing:** K8s routes traffic to WebApi before it has finished connecting to PostgreSQL and Redis. Requests fail until the service is fully initialized.
2. **Stuck pods:** If a worker service reaches a broken state (e.g. Redis connection permanently lost), K8s does not know to restart it.

### Design

**WebApi** — already uses `WebApplication`, so health check middleware is straightforward:

```csharp
builder.Services.AddHealthChecks()
    .AddNpgsql(connectionString, name: "postgres")
    .AddRedis(redisConnString,   name: "redis");

// Liveness: always 200 — "process is not hung"
app.MapHealthChecks("/health/live",  new HealthCheckOptions { Predicate = _ => false });
// Readiness: checks DB + Redis — "dependencies are reachable"
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = _ => true });
```

**Worker services** (JobAutomator, JobOrchestrator, WorkflowHost) — currently use `Host.CreateApplicationBuilder(args)` with no HTTP listener. Switch to `WebApplication.CreateBuilder(args)` and expose a dedicated health port (8080):

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://+:8080");

builder.Services.AddHealthChecks()
    .AddRedis(redisConnString, name: "redis");  // each service adds what it depends on

var app = builder.Build();
app.MapHealthChecks("/health/live",  new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = _ => true });

await app.RunAsync();
```

**NuGet packages to add** (per project):
- `AspNetCore.HealthChecks.NpgsqlServer` — `AddNpgsql()` extension
- `AspNetCore.HealthChecks.Redis` — `AddRedis()` extension

**K8s probe configuration** (example for WebApi):
```yaml
livenessProbe:
  httpGet:
    path: /health/live
    port: 8080
  initialDelaySeconds: 5
  periodSeconds: 10

readinessProbe:
  httpGet:
    path: /health/ready
    port: 8080
  initialDelaySeconds: 10
  periodSeconds: 5
  failureThreshold: 3
```

**Key constraints:**
- Liveness must never check dependencies — only that the process itself is responsive. If Redis goes down, the pod should NOT be killed (it will recover); it should only be marked not-ready.
- Readiness failing is safe and expected during startup. Liveness failing causes a pod restart — use it only for true deadlock/hang detection.
- WorkflowEngine is a short-lived child process; it does not need health probes.

### Files to Create / Modify

- `WebApi/Program.cs` — add `AddHealthChecks()`, `MapHealthChecks()`
- `JobAutomator/Program.cs` — switch to `WebApplication.CreateBuilder`, add health endpoints
- `JobOrchestrator/Program.cs` — same
- `WorkflowHost/Program.cs` — same
- `*.csproj` for each worker service — add health check NuGet packages
- `deploy/docker/compose.yaml` — add `healthcheck:` blocks for service containers

---

## 3. OpenTelemetry Metrics

### Problem

OpenTelemetry distributed tracing is fully implemented (spans flow across all services). But there are no metrics. In production, this means:

- No dashboards showing job throughput, queue depth, or execution latency
- No alerting on error rates or trigger evaluation failures
- No visibility into which host groups are busiest or which automations fire most often

The OTel infrastructure already exists (`TelemetryExtensions`, OTLP exporter, Jaeger/Tempo config). Adding metrics is a natural extension of what is already there.

### Design

Add a `FlowForgeMetrics` static class alongside `FlowForgeActivitySources`, following the same pattern:

```csharp
// FlowForge.Infrastructure/Telemetry/FlowForgeMetrics.cs
public static class FlowForgeMetrics
{
    public const string MeterName = "FlowForge";

    private static readonly Meter _meter = new(MeterName);

    // Counters
    public static readonly Counter<long> JobsCreated   = _meter.CreateCounter<long>("flowforge.jobs.created");
    public static readonly Counter<long> JobsCompleted = _meter.CreateCounter<long>("flowforge.jobs.completed");
    public static readonly Counter<long> JobsFailed    = _meter.CreateCounter<long>("flowforge.jobs.failed");
    public static readonly Counter<long> TriggersFired = _meter.CreateCounter<long>("flowforge.triggers.fired");

    // Histograms
    public static readonly Histogram<double> JobDurationSeconds =
        _meter.CreateHistogram<double>("flowforge.jobs.duration_seconds");
}
```

**Update `AddFlowForgeTelemetry`** to wire up the metrics pipeline:

```csharp
services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(serviceName))
    .WithTracing(/* existing */)
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter(FlowForgeMetrics.MeterName)
            .AddRuntimeInstrumentation();   // GC, thread pool, memory

        if (!string.IsNullOrEmpty(otlpEndpoint))
            metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
    });
```

**Instrumentation points:**

| Metric | Where to record | Tags |
|---|---|---|
| `flowforge.jobs.created` | `AutomationTriggeredConsumer` after job saved | `host_group_id`, `automation_id` |
| `flowforge.jobs.completed` | `JobStatusChangedConsumer` on `Completed` | `host_group_id` |
| `flowforge.jobs.failed` | `JobStatusChangedConsumer` on `Error` / `CompletedUnsuccessfully` | `host_group_id` |
| `flowforge.triggers.fired` | `AutomationWorker` after publishing event | `automation_id` |
| `flowforge.jobs.duration_seconds` | `WorkflowEngine/Program.cs` on exit | `task_id` |

**NuGet packages to add** (to `FlowForge.Infrastructure.csproj`):
- `OpenTelemetry.Instrumentation.Runtime` — GC, thread pool, memory metrics

**NuGet packages to add** (to each service csproj):
- `OpenTelemetry.Instrumentation.Process` — CPU and memory per-process (optional)

**Key constraint:** `Meter` instances must be singletons. Using `static readonly` on `FlowForgeMetrics` ensures this. Do not `new Meter(...)` in constructors — it defeats instrumentation.

### Files to Create / Modify

- `FlowForge.Infrastructure/Telemetry/FlowForgeMetrics.cs` — new static metrics class
- `FlowForge.Infrastructure/Telemetry/TelemetryExtensions.cs` — add `.WithMetrics()`
- `FlowForge.Infrastructure/FlowForge.Infrastructure.csproj` — add OTel runtime instrumentation
- `WebApi/Workers/AutomationTriggeredConsumer.cs` — record `JobsCreated`
- `WebApi/Workers/JobStatusChangedConsumer.cs` — record `JobsCompleted` / `JobsFailed`
- `JobAutomator/Workers/AutomationWorker.cs` — record `TriggersFired`
- `WorkflowEngine/Program.cs` — record `JobDurationSeconds`

---

## 4. Job Timeout Enforcement

### Problem

Jobs run indefinitely. If a workflow handler hangs — network call with no timeout, SQL query against a locked table, Python subprocess waiting for input — the `WorkflowEngine` process runs forever. The host remains marked busy. The `ActiveJobId` lock on the `Automation` never clears. The automation cannot fire again.

There is currently no way to bound job execution time, which makes the system unusable for any automation with SLA requirements.

### Design

Add `TimeoutSeconds: int?` (nullable = no timeout) to the `Automation` aggregate. The value is propagated forward through the event chain to the `Job` entity, where it is enforced by `WorkflowEngine` at runtime.

**Domain change:**
```csharp
// Automation.cs
public int? TimeoutSeconds { get; private set; }

// Factory method / update method — allow setting TimeoutSeconds (null = unlimited)
```

**Propagation chain:**
```
Automation.TimeoutSeconds
  → AutomationTriggeredEvent.TimeoutSeconds
  → Job.TimeoutSeconds (stored in job DB, never changes after creation)
  → WorkflowEngine reads from Job on startup
```

**`WorkflowEngine/Program.cs` enforcement:**
```csharp
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Link a timeout token if configured
if (job.TimeoutSeconds.HasValue)
{
    var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(job.TimeoutSeconds.Value));
    var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeoutCts.Token);
    cts = linkedCts;
}
```

On timeout, the `OperationCanceledException` is caught by the existing cancellation handler, and the engine exits with `JobStatus.Error` and `message = $"Job timed out after {job.TimeoutSeconds} seconds."`.

**Why propagate through events rather than re-reading from Automation at execution time:**
- `WorkflowEngine` is isolated; it only has a database connection, not the platform DB
- The timeout is a property of the *specific job run*, not always the current automation setting (an admin could change `TimeoutSeconds` mid-run)
- Storing it on `Job` creates an immutable audit record: "this job was created with a 300s timeout"

**Migrations required:**
- `ALTER TABLE "Automations" ADD "TimeoutSeconds" int NULL`
- `ALTER TABLE "Jobs" ADD "TimeoutSeconds" int NULL` (in jobs DB migration)

### Files to Create / Modify

- `FlowForge.Domain/Entities/Automation.cs` — add `TimeoutSeconds`
- `FlowForge.Domain/Entities/Job.cs` — add `TimeoutSeconds`
- `FlowForge.Contracts/Events/AutomationTriggeredEvent.cs` — add `TimeoutSeconds`
- `FlowForge.Contracts/Events/JobCreatedEvent.cs` — add `TimeoutSeconds`
- `FlowForge.Infrastructure/Persistence/Platform/Configurations/AutomationConfiguration.cs` — map column
- `FlowForge.Infrastructure/Persistence/Jobs/Configurations/JobConfiguration.cs` — map column
- `WebApi/Workers/AutomationTriggeredConsumer.cs` — pass `TimeoutSeconds` when creating Job
- `WorkflowEngine/Program.cs` — linked timeout `CancellationTokenSource`
- `WebApi/Services/AutomationService.cs` — accept and persist `TimeoutSeconds` on create/update
- Migrations: one for platform DB, one for jobs DB

---

## 5. Dead Letter Queue for Poison Messages

### Problem

When a worker throws an unhandled exception while processing a Redis Stream message, the current behavior is:
- The message is **acknowledged** (`XACK`) before the exception is caught — or in some consumers the exception escapes and the message is never acked, causing it to sit in the pending-entry list forever
- The error is logged, but the original message payload is gone from the processing context
- There is no way to inspect what message caused the failure, replay it, or discard it deliberately

This is a **silent reliability gap**: bad data (malformed payloads, unexpected nulls, schema mismatches after a deploy) can cause events to be dropped with no operator-visible record.

### Design

Add a `flowforge:dlq` Redis Stream. When any consumer catches a non-transient processing exception, it writes the failed message to the DLQ before acknowledging, then continues.

**`IDlqWriter` interface** (new, in Infrastructure):
```csharp
public interface IDlqWriter
{
    Task WriteAsync(string sourceStream, string messageId, string payload, string error, CancellationToken ct = default);
}
```

**`DlqWriter` implementation:**
```csharp
public class DlqWriter(IConnectionMultiplexer redis) : IDlqWriter
{
    private readonly IDatabase _db = redis.GetDatabase();

    public async Task WriteAsync(string sourceStream, string messageId, string payload, string error, CancellationToken ct = default)
    {
        await _db.StreamAddAsync(StreamNames.Dlq,
        [
            new NameValueEntry("sourceStream", sourceStream),
            new NameValueEntry("messageId",    messageId),
            new NameValueEntry("payload",      payload),
            new NameValueEntry("error",        error),
            new NameValueEntry("failedAt",     DateTimeOffset.UtcNow.ToString("O"))
        ]);
    }
}
```

**Consumer change pattern** (apply to all consumers — `AutomationTriggeredConsumer`, `JobStatusChangedConsumer`, `JobDispatcherWorker`, etc.):
```csharp
foreach (var entry in entries)
{
    try
    {
        // ... existing processing logic ...
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to process message {MessageId} from {Stream}. Sending to DLQ.", entry.Id, streamName);
        await dlqWriter.WriteAsync(streamName, entry.Id, payload, ex.Message, ct);
    }
    finally
    {
        await _db.StreamAcknowledgeAsync(streamName, consumerGroup, entry.Id);
    }
}
```

**`XACK` moves to `finally`** — this is the critical behavioral change. Currently some consumers ack inside the try block (message lost on exception), others ack after processing (message hangs in pending list on exception). With DLQ, the finally block always acks — the event is never re-delivered, but it is captured in the DLQ for inspection.

**WebApi DLQ endpoint:**
```
GET  /api/dlq?limit=50            → list recent DLQ entries (sourceStream, payload, error, failedAt)
DELETE /api/dlq/{messageId}       → discard (no action needed beyond acknowledgement of review)
POST /api/dlq/{messageId}/replay  → re-publish payload to its sourceStream
```

**`StreamNames.Dlq` constant:** Add `"flowforge:dlq"` to `StreamNames.cs`.

**Key constraints:**
- DLQ writes must not throw — if Redis is down, log and continue. A failed DLQ write is worse than no DLQ at all.
- DLQ messages are not consumer-group-consumed (they are read directly via `XRANGE`). No consumer group needed for the DLQ itself.
- `IDlqWriter` is registered as singleton alongside `IMessagePublisher` and `IMessageConsumer`.

### Files to Create / Modify

- `FlowForge.Infrastructure/Messaging/DeadLetter/IDlqWriter.cs` — new interface
- `FlowForge.Infrastructure/Messaging/DeadLetter/DlqWriter.cs` — new implementation
- `FlowForge.Infrastructure/Messaging/Redis/StreamNames.cs` — add `Dlq` constant
- `FlowForge.Infrastructure/ServiceCollectionExtensions.cs` — register `IDlqWriter` as singleton
- `WebApi/Workers/AutomationTriggeredConsumer.cs` — inject `IDlqWriter`, wrap in try/catch/finally
- `WebApi/Workers/JobStatusChangedConsumer.cs` — same
- `JobOrchestrator/Workers/JobDispatcherWorker.cs` — same
- `WorkflowHost/Workers/JobConsumerWorker.cs` — same
- `WebApi/Controllers/DlqController.cs` — new REST controller

---

## 6. Job Auto-Retry on Failure

### Problem

When a job reaches a terminal failure status (`Error` or `CompletedUnsuccessfully`), the automation that triggered it is permanently unblocked (its `ActiveJobId` is cleared), but the failed work is silently abandoned. Users must notice the failure and manually re-enable/re-trigger the automation to get the job re-run. For transient failures (network blip, database timeout, temporary resource exhaustion), this is unacceptable — the system should retry automatically.

### Design

Add two new fields to the `Automation` aggregate:

```csharp
public int MaxRetries { get; private set; }       // 0 = no retry (default)
```

Add one new field to the `Job` entity (jobs DB):

```csharp
public int RetryAttempt { get; private set; }     // 0 = first attempt
```

**Propagation chain:**
```
Automation.MaxRetries
  → AutomationTriggeredEvent.MaxRetries + RetryAttempt
  → Job.MaxRetries + Job.RetryAttempt (both stored in job DB)
```

**Retry logic in `JobStatusChangedConsumer`:**

```csharp
// After clearing ActiveJobId on failure status:
if (@event.NewStatus is JobStatus.Error or JobStatus.CompletedUnsuccessfully)
{
    if (job.RetryAttempt < job.MaxRetries)
    {
        // Re-trigger: publish a new AutomationTriggeredEvent with RetryAttempt + 1
        await outboxWriter.WriteAsync(new AutomationTriggeredEvent(
            AutomationId:  automation.Id,
            HostGroupId:   automation.HostGroupId,
            ConnectionId:  automation.ConnectionId,
            TaskId:        automation.TaskId,
            TriggeredAt:   DateTimeOffset.UtcNow,
            MaxRetries:    job.MaxRetries,
            RetryAttempt:  job.RetryAttempt + 1));

        logger.LogInformation(
            "Job {JobId} failed (attempt {Attempt}/{Max}); scheduling retry.",
            job.Id, job.RetryAttempt + 1, job.MaxRetries);
    }
}
```

**`AutomationTriggeredConsumer`** already reads the event and creates a `Job`. It sets `job.RetryAttempt = event.RetryAttempt` and `job.MaxRetries = event.MaxRetries` when building the new job.

**Duplicate prevention interaction:** When a retry re-triggers, `automation.ActiveJobId` has already been cleared by the terminal status handler (same transaction, earlier in `JobStatusChangedConsumer`). The duplicate-prevention check will not block the retry.

**Why no delay between retries (for now):** Introducing a delayed retry requires a deferred-delivery mechanism (e.g. a delayed stream, a scheduled Quartz job, or a sleep in the consumer). That complexity is a separate item. Immediate retry is correct for transient failures. A future item can add configurable `RetryDelaySeconds`.

**Migrations required:**
- `ALTER TABLE "Automations" ADD "MaxRetries" int NOT NULL DEFAULT 0`
- `ALTER TABLE "Jobs" ADD "RetryAttempt" int NOT NULL DEFAULT 0`
- `ALTER TABLE "Jobs" ADD "MaxRetries" int NOT NULL DEFAULT 0` (in jobs DB)

### Files to Create / Modify

- `FlowForge.Domain/Entities/Automation.cs` — add `MaxRetries`
- `FlowForge.Domain/Entities/Job.cs` — add `RetryAttempt`, `MaxRetries`
- `FlowForge.Contracts/Events/AutomationTriggeredEvent.cs` — add `MaxRetries`, `RetryAttempt`
- `FlowForge.Infrastructure/Persistence/Platform/Configurations/AutomationConfiguration.cs` — map `MaxRetries`
- `FlowForge.Infrastructure/Persistence/Jobs/Configurations/JobConfiguration.cs` — map both fields
- `WebApi/Workers/AutomationTriggeredConsumer.cs` — pass retry fields when creating Job
- `WebApi/Workers/JobStatusChangedConsumer.cs` — retry logic after terminal status
- `WebApi/Services/AutomationService.cs` — accept and persist `MaxRetries` on create/update
- Migrations: platform DB + jobs DB

---

## 7. Configurable Polling Intervals via IOptions

### Problem

All timing constants in the system are hardcoded as literals scattered across worker classes:

| Location | Constant | Value |
|---|---|---|
| `AutomationWorker` | Evaluation cycle | `5 seconds` |
| `OutboxRelayWorker` | Poll interval | `500 ms` |
| `OutboxRelayWorker` | Batch size | `50` |
| `HeartbeatMonitorWorker` | Check interval | `15 seconds` |
| `HostHeartbeatWorker` | Publish interval | `5 seconds` |
| `RedisStreamConsumer` | Poll delay (empty stream) | `100 ms` |

Changing any of these requires a code change, a build, and a redeployment. In production, this means inability to tune for different load profiles without touching source code. It also makes testing harder — tests must live with production delays or resort to Thread.Sleep hacks.

### Design

One strongly-typed options class per configurable component. Each class lives alongside the worker it configures and is registered with `services.Configure<TOptions>(config.GetSection("..."))`.

```csharp
// JobAutomator
public class AutomationWorkerOptions
{
    public const string SectionName = "AutomationWorker";
    public int EvaluationIntervalSeconds { get; init; } = 5;
}

// WebApi
public class OutboxRelayOptions
{
    public const string SectionName = "OutboxRelay";
    public int PollIntervalMs { get; init; } = 500;
    public int BatchSize      { get; init; } = 50;
}

// JobOrchestrator
public class HeartbeatMonitorOptions
{
    public const string SectionName = "HeartbeatMonitor";
    public int CheckIntervalSeconds { get; init; } = 15;
}

// WorkflowHost
public class HostHeartbeatOptions
{
    public const string SectionName = "HostHeartbeat";
    public int PublishIntervalSeconds { get; init; } = 5;
}
```

Workers inject `IOptions<TOptions>` and read the value:
```csharp
public class AutomationWorker(
    /* ... */,
    IOptions<AutomationWorkerOptions> options) : BackgroundService
{
    private readonly AutomationWorkerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ...
        await Task.Delay(TimeSpan.FromSeconds(_options.EvaluationIntervalSeconds), stoppingToken);
    }
}
```

**Add default values to all `appsettings.json` files** so the configuration is visible and documented:
```json
{
  "AutomationWorker": {
    "EvaluationIntervalSeconds": 5
  }
}
```

**Key constraint:** Use `IOptions<T>` (not `IOptionsMonitor<T>`). These intervals are read once at startup. Hot-reload of polling intervals would cause unpredictable behavior in running loops — the simpler, safer choice is to require a restart for interval changes.

### Files to Create / Modify

- `JobAutomator/Options/AutomationWorkerOptions.cs` — new
- `JobAutomator/Workers/AutomationWorker.cs` — inject and use options
- `JobAutomator/Program.cs` — `services.Configure<AutomationWorkerOptions>(...)`
- `JobAutomator/appsettings.json` — add `AutomationWorker` section
- `WebApi/Options/OutboxRelayOptions.cs` — new
- `WebApi/Workers/OutboxRelayWorker.cs` — inject and use options
- `WebApi/Program.cs` — `services.Configure<OutboxRelayOptions>(...)`
- `WebApi/appsettings.json` — add `OutboxRelay` section
- `JobOrchestrator/Options/HeartbeatMonitorOptions.cs` — new
- `JobOrchestrator/Workers/HeartbeatMonitorWorker.cs` — inject and use options
- `JobOrchestrator/Program.cs` — `services.Configure<HeartbeatMonitorOptions>(...)`
- `JobOrchestrator/appsettings.json` — add `HeartbeatMonitor` section
- `WorkflowHost/Options/HostHeartbeatOptions.cs` — new
- `WorkflowHost/Workers/HostHeartbeatWorker.cs` — inject and use options
- `WorkflowHost/Program.cs` — `services.Configure<HostHeartbeatOptions>(...)`
- `WorkflowHost/appsettings.json` — add `HostHeartbeat` section

---

## Not Planned (Yet)

| Item | Reason deferred |
|---|---|
| Authentication / OAuth2 | Explicitly out of scope (see AGENTS.md) |
| Multi-tenancy | Explicitly out of scope (see AGENTS.md) |
| Frontend UI | Explicitly out of scope (see AGENTS.md) |
| Reusable named custom trigger definitions | Explicitly out of scope (see AGENTS.md) |
| JobOrchestrator distributed state (Redis counter) | Acceptable for current scale; revisit when replicas needed |
| Job retry delay / backoff | Natural follow-up to item #6; requires delayed delivery mechanism |
| Custom script subprocess sandbox | Security hardening; important for untrusted multi-tenant use |
