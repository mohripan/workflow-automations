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
| 1 | [Redis Consumer Group Bootstrap](#1-redis-consumer-group-bootstrap) | Bug / Reliability | `[x]` |
| 2 | [Webhook Secret Validation](#2-webhook-secret-validation) | Security | `[x]` |
| 3 | [Duplicate Job Prevention](#3-duplicate-job-prevention) | Business Logic | `[x]` |
| 4 | [Transactional Outbox Pattern](#4-transactional-outbox-pattern) | Reliability | `[x]` |
| 5 | [OpenTelemetry Distributed Tracing](#5-opentelemetry-distributed-tracing) | Observability | `[ ]` |
| 6 | [Quartz Clustering](#6-quartz-clustering) | Scalability | `[ ]` |
| 7 | [Startup Resilience for AutomationCacheInitializer](#7-startup-resilience-for-automationcacheinitializer) | Reliability | `[ ]` |

---

## 1. Redis Consumer Group Bootstrap

### Problem

`IMessageConsumer` calls `XREADGROUP` on startup. Redis requires the consumer group to be created with `XGROUP CREATE ... MKSTREAM` before any consumer can join it. On a fresh Redis instance — or after a Redis flush — all consumers crash immediately with a `NOGROUP` exception because the groups do not exist yet.

This is a **silent crash bug**: the service starts, the hosted service begins executing, and then immediately dies without a clear error message unless structured logging is in place.

### Affected Services

- `FlowForge.WebApi` (consumes `AutomationTriggered`, `JobStatusChanged`)
- `FlowForge.JobAutomator` (consumes `AutomationChanged`, `JobStatusChanged`)
- `FlowForge.JobOrchestrator` (consumes `JobCreated`)
- `FlowForge.WorkflowHost` (consumes `host:{hostId}`, `JobCancelRequested`)

### Design

Add a `BootstrapAsync` method to `IMessageConsumer` (or a separate `IStreamBootstrapper`) that each service calls during startup — before any worker begins consuming. It creates the stream and group if they don't already exist using `XGROUP CREATE mystream mygroup $ MKSTREAM`.

```
IStreamBootstrapper.EnsureAsync(streamName, groupName)
  → XGROUP CREATE {streamName} {groupName} $ MKSTREAM
  → Idempotent: if group already exists (BUSYGROUP error), swallow and continue
```

This call belongs in `Program.cs` (or a dedicated `IHostedService` that runs before consumers start) for each service, listing every stream+group that service reads from.

**Key constraint:** Use `$` as the starting ID — not `0`. Starting from `0` would re-deliver all historical messages on every restart, which is incorrect behaviour.

### Files to Create / Modify

- `FlowForge.Infrastructure/Messaging/Abstractions/IStreamBootstrapper.cs` — new interface
- `FlowForge.Infrastructure/Messaging/Redis/RedisStreamBootstrapper.cs` — implementation using `XGROUP CREATE ... MKSTREAM`
- `ServiceCollectionExtensions.cs` — register `IStreamBootstrapper` as singleton
- `Program.cs` in each consuming service — call `EnsureAsync` for every stream+group the service reads

---

## 2. Webhook Secret Validation

### Problem

`AutomationService.FireWebhookAsync` currently accepts any request without validating the `X-Webhook-Secret` header. The `secretHash` field exists in `WebhookTriggerDescriptor`'s config schema and is described as a BCrypt hash of the expected secret, but the validation is not implemented. Any caller — authenticated or not — can fire any automation's webhook trigger.

### Design

**Secret storage:** The `WebhookTriggerConfig.SecretHash` field stores a BCrypt hash of the secret. The plain-text secret is never stored anywhere in FlowForge. The user configures it once, FlowForge hashes it and saves only the hash.

**Validation flow in `FireWebhookAsync`:**

```
1. Load automation → check IsEnabled (already done)
2. Load webhook trigger → deserialize WebhookTriggerConfig
3. If config.SecretHash is null or empty → accept (unauthenticated webhook allowed)
4. If config.SecretHash is set:
     a. If request secret header is null → 401 Unauthorized
     b. BCrypt.Verify(requestSecret, config.SecretHash) → if false → 401 Unauthorized
5. Set Redis flag → return 202 Accepted
```

**Why BCrypt:** Timing-safe by design (constant-time comparison built in). Stores only a hash so a DB leak does not expose the secret. Industry standard for this pattern.

**NuGet package:** `BCrypt.Net-Next` — add to `FlowForge.WebApi.csproj` only (this logic lives in the service layer, not Infrastructure).

**New exception:** `UnauthorizedWebhookException` already exists in `DomainException.cs`. Map it to HTTP 401 in `ExceptionHandlingMiddleware`.

### Files to Modify

- `FlowForge.WebApi/Services/AutomationService.cs` — implement validation in `FireWebhookAsync`
- `FlowForge.WebApi/Middleware/ExceptionHandlingMiddleware.cs` — map `UnauthorizedWebhookException` → 401
- `FlowForge.WebApi.csproj` — add `BCrypt.Net-Next`

---

## 3. Duplicate Job Prevention

### Problem

`AutomationWorker` runs on a fixed interval. If an automation's condition is met and the previous job is still `InProgress`, a second `AutomationTriggeredEvent` is published and a second job is created. There is currently no guard against this. For most real-world automations (ETL pipelines, report generation, scheduled tasks) this is wrong — a second instance should not start until the first finishes.

### Design

Add an `ActiveJobId` nullable field to the `Automation` aggregate. This field tracks the ID of the most recently created job that has not yet reached a terminal status.

**Terminal statuses** (where `ActiveJobId` should be cleared): `Completed`, `CompletedUnsuccessfully`, `Error`, `Cancelled`, `Removed`.

**Lifecycle:**

```
AutomationTriggeredConsumer (WebApi):
  1. Load automation
  2. Check automation.ActiveJobId
     → If set: verify job status (load from job repo)
       → If job is NOT in terminal state: discard event, log warning, return
       → If job IS in terminal state: clear ActiveJobId and proceed (handles crash/missed event)
  3. Create job
  4. Call automation.SetActiveJob(job.Id)
  5. Save automation
  6. Publish JobCreatedEvent

JobStatusChangedConsumer (WebApi):
  → When new status is terminal:
    1. Load automation by job.AutomationId
    2. Call automation.ClearActiveJob()
    3. Save automation
```

**Domain changes to `Automation`:**

```csharp
public Guid? ActiveJobId { get; private set; }

public void SetActiveJob(Guid jobId)  => ActiveJobId = jobId;
public void ClearActiveJob()          => ActiveJobId = null;
```

**Why on the Automation entity (platform DB) and not a query against the jobs DB:**
- The jobs DB is per-host-group. Querying it from the WebApi at trigger time creates a cross-database dependency.
- Writing to `Automation` keeps the check within the platform DB transaction, which is already being saved.
- It is a natural piece of Automation state: "what is this automation currently doing?"

**Edge case — orphaned `ActiveJobId`:** If a job reaches terminal state but the consumer misses the event (Redis crash, etc.), the automation would be stuck. The fallback in step 2 above handles this: on the next trigger, if `ActiveJobId` is set but the job is already terminal, clear it and proceed normally.

**New migration required** — add `ActiveJobId uuid` nullable column to `Automations` table.

### Files to Create / Modify

- `FlowForge.Domain/Entities/Automation.cs` — add `ActiveJobId`, `SetActiveJob()`, `ClearActiveJob()`
- `FlowForge.Infrastructure/Persistence/Platform/Configurations/AutomationConfiguration.cs` — map `ActiveJobId`
- `FlowForge.WebApi/Workers/AutomationTriggeredConsumer.cs` — add duplicate check logic
- `FlowForge.WebApi/Workers/JobStatusChangedConsumer.cs` — call `ClearActiveJob()` on terminal status
- Migration: `dotnet ef migrations add AddActiveJobIdToAutomation`

---

## 4. Transactional Outbox Pattern

### Problem

Every mutation in `AutomationService` does two things: save to the platform DB, then publish an event to Redis. These are two separate I/O operations with no transaction spanning both. If Redis is unavailable at publish time — or if the process crashes between the two operations — the DB save succeeds but the event is never delivered. This leaves:

- `JobAutomator`'s cache permanently stale (never learns about a new automation)
- `JobOrchestrator` never receiving a `JobCreatedEvent`
- Frontend never receiving a `JobStatusChangedEvent` via SignalR

This is a **data consistency gap** that gets worse at scale and under failure conditions.

### Design

**The pattern:** Instead of publishing to Redis directly from the service, write the event payload to an `OutboxMessages` table inside the **same database transaction** as the entity change. A separate background worker (the "outbox relay") polls the table and publishes pending messages to Redis, then marks them as sent.

```
Service method:
  BEGIN TRANSACTION
    1. Save entity change (e.g. automation.Enable())
    2. INSERT INTO OutboxMessages (Id, EventType, Payload, CreatedAt, SentAt=null)
  COMMIT TRANSACTION
  ↓
  (Redis publish happens asynchronously — no longer in the request path)

OutboxRelayWorker (background):
  LOOP every ~500ms:
    1. SELECT TOP N FROM OutboxMessages WHERE SentAt IS NULL ORDER BY CreatedAt
    2. FOR EACH message:
         publisher.PublishAsync(deserialize payload)
         UPDATE OutboxMessages SET SentAt = now WHERE Id = message.Id
```

**Outbox table** lives in `PlatformDbContext` (platform DB). Job-related events (`JobCreatedEvent`, `JobStatusChangedEvent`) are published from `AutomationTriggeredConsumer` and `WorkflowEngine` — these services already have access to their respective databases, so the outbox for those can live in their respective DB contexts if needed. Start with the platform DB outbox for automation events.

**`OutboxMessage` entity:**

```csharp
public class OutboxMessage : BaseEntity<Guid>
{
    public string EventType { get; private set; } = default!;  // full type name
    public string Payload   { get; private set; } = default!;  // JSON-serialized event
    public string StreamName { get; private set; } = default!; // target Redis stream
    public DateTimeOffset? SentAt { get; private set; }

    public void MarkSent() => SentAt = DateTimeOffset.UtcNow;
}
```

**Key constraints:**
- The relay worker must be idempotent — if it publishes a message and crashes before marking `SentAt`, it will re-publish on the next pass. Redis Stream consumers must tolerate duplicate messages (they already should, via consumer group `XACK`).
- Use a short poll interval (500ms) rather than long-polling — the goal is near-realtime delivery with at-least-once guarantees.
- `SentAt` is never null-checked in business logic — it's purely a relay concern.

### Files to Create / Modify

- `FlowForge.Domain/Entities/OutboxMessage.cs` — new entity
- `FlowForge.Infrastructure/Persistence/Platform/PlatformDbContext.cs` — add `DbSet<OutboxMessage>`
- `FlowForge.Infrastructure/Persistence/Platform/Configurations/OutboxMessageConfiguration.cs` — new EF config
- `FlowForge.Infrastructure/Messaging/Outbox/IOutboxWriter.cs` — new interface (used by services to write outbox entries)
- `FlowForge.Infrastructure/Messaging/Outbox/OutboxWriter.cs` — EF Core implementation
- `FlowForge.WebApi/Workers/OutboxRelayWorker.cs` — polls outbox, publishes, marks sent
- `FlowForge.WebApi/Services/AutomationService.cs` — replace `publisher.PublishAsync` with `outboxWriter.WriteAsync`
- `FlowForge.WebApi/Workers/AutomationTriggeredConsumer.cs` — same replacement for `JobCreatedEvent`
- Migration: `dotnet ef migrations add AddOutboxMessages`

---

## 5. OpenTelemetry Distributed Tracing

### Problem

A job's journey touches 5 services: `JobAutomator` → `WebApi` → `JobOrchestrator` → `WorkflowHost` → `WorkflowEngine`. Currently there is no correlation between log entries across these services. Diagnosing a failed job requires manually cross-referencing logs by `JobId` across 5 different log streams.

### Design

Use **OpenTelemetry** with **traces propagated through Redis Stream message headers**.

**NuGet packages** (add to each service):
- `OpenTelemetry.Extensions.Hosting`
- `OpenTelemetry.Instrumentation.AspNetCore` (WebApi only)
- `OpenTelemetry.Exporter.Otlp` (exports to Jaeger, Tempo, etc.)

**Trace propagation through Redis Streams:**

HTTP has standard `traceparent` headers. Redis Streams do not. The solution is to include the trace context as an extra field in every published stream message. `RedisStreamPublisher` serializes and injects it; `RedisStreamConsumer` extracts and restores it before calling the handler.

```
IMessagePublisher.PublishAsync:
  → Serialize event payload as usual
  → Extract current Activity.Current traceparent → add as extra field "traceparent"
  → XADD stream * payload ... traceparent <value>

IMessageConsumer.ConsumeAsync:
  → Read stream entry
  → Extract "traceparent" field
  → Restore parent context via ActivityContext.Parse(traceparent)
  → Start new Activity as child of restored context
  → Yield event to consumer
```

**Span naming convention:**
- `publish {StreamName}` — for outgoing messages
- `consume {StreamName}` — for incoming messages
- `evaluate automation {AutomationId}` — in `AutomationWorker`
- `dispatch job {JobId}` — in `JobDispatcherWorker`
- `execute job {JobId}` — in `WorkflowEngine`

**`ActivitySource` naming:** `FlowForge.{ServiceName}` — e.g. `FlowForge.JobAutomator`, `FlowForge.WebApi`.

**`CorrelationId`:** The `TraceId` from the OpenTelemetry span serves as the correlation ID. No separate `CorrelationId` field is needed on events.

**Local dev setup:** Add a Jaeger (or Grafana Tempo) container to `deploy/docker/compose.yaml`:

```yaml
jaeger:
  image: jaegertracing/all-in-one:latest
  ports:
    - "16686:16686"   # Jaeger UI
    - "4317:4317"     # OTLP gRPC
```

### Files to Create / Modify

- `FlowForge.Infrastructure/Telemetry/TelemetryExtensions.cs` — `AddFlowForgeTelemetry(IServiceCollection)` extension
- `FlowForge.Infrastructure/Messaging/Redis/RedisStreamPublisher.cs` — inject traceparent header
- `FlowForge.Infrastructure/Messaging/Redis/RedisStreamConsumer.cs` — extract and restore traceparent
- `ServiceCollectionExtensions.cs` — call `AddFlowForgeTelemetry` from `AddInfrastructure`
- `Program.cs` in each service — configure OTLP exporter endpoint
- `deploy/docker/compose.yaml` — add Jaeger service

---

## 6. Quartz Clustering

### Problem

`JobAutomator` currently uses Quartz with an **in-memory job store**. This has two problems:

1. **No HA:** If the pod crashes, all scheduled jobs are lost until the next restart and `QuartzScheduleSync` re-registers them (which requires the cache to be seeded first — another dependency).
2. **Duplicate fires on multiple replicas:** If you scale `JobAutomator` to 2+ replicas (for availability), each replica will have its own independent Quartz scheduler. The same `cronExpression` will fire on both replicas at the same time, producing duplicate `AutomationTriggeredEvent`s.

### Design

Switch Quartz to use **PostgreSQL as the job store** via `Quartz.Serialization.Json` and `Quartz.Persistence.EntityFrameworkCore` (or the simpler ADO.NET provider). Quartz clustering uses a `QRTZ_*` table set in a database — only one node in the cluster fires each scheduled job.

**Database:** Use a dedicated `flowforge_quartz` PostgreSQL database (or a separate schema in `flowforge_platform`). Do not use the job-group databases — Quartz schema is infrastructure, not domain data.

**Configuration:**

```json
{
  "Quartz": {
    "quartz.scheduler.instanceName": "FlowForgeScheduler",
    "quartz.scheduler.instanceId": "AUTO",
    "quartz.jobStore.type": "Quartz.Impl.AdoJobStore.JobStoreTX",
    "quartz.jobStore.driverDelegateType": "Quartz.Impl.AdoJobStore.PostgreSQLDelegate",
    "quartz.jobStore.dataSource": "default",
    "quartz.jobStore.clustered": "true",
    "quartz.jobStore.clusterCheckinInterval": "20000",
    "quartz.dataSource.default.connectionString": "...",
    "quartz.dataSource.default.provider": "Npgsql"
  }
}
```

**`QuartzScheduleSync` change:** The `SyncAsync` method becomes idempotent across replicas — Quartz's clustered store ensures each job is registered once regardless of how many `JobAutomator` instances call `ScheduleJob` for the same key. No application-level locking is needed.

**`QRTZ_*` tables:** Created by running the Quartz PostgreSQL DDL script provided in the Quartz.NET repository. Add this to the `deploy/docker/` setup scripts or a migration-equivalent.

### Files to Modify

- `FlowForge.JobAutomator/Initialization/QuartzScheduleSync.cs` — no logic change; clustering is transparent
- `FlowForge.JobAutomator/Program.cs` — configure Quartz with ADO.NET store instead of RAM store
- `FlowForge.JobAutomator/appsettings.json` — add Quartz DB configuration
- `FlowForge.JobAutomator.csproj` — add `Quartz.Serialization.Json`, ADO.NET Quartz provider packages
- `deploy/docker/compose.yaml` — add `postgres-quartz` service (or reuse platform DB with a separate schema)
- `deploy/docker/` — add Quartz DDL init script

---

## 7. Startup Resilience for AutomationCacheInitializer

### Problem

`AutomationCacheInitializer.StartAsync` makes an HTTP call to `GET /api/automations/snapshots`. If the Web API is not yet ready (container startup race condition, health check delay, etc.), the call fails and the service **throws on startup**, which crashes the entire `JobAutomator` process.

In a Kubernetes environment this triggers a pod restart loop. Even with `readinessProbe` on the Web API, the race window exists.

### Design

Wrap the snapshot fetch in a **retry loop with exponential backoff** using `Polly`. The service should not crash on a transient startup failure — it should wait and retry until it either succeeds or the host cancellation token is fired (indicating a deliberate shutdown).

```
Retry policy:
  - Retry indefinitely (until stoppingToken cancelled)
  - Backoff: 2s → 4s → 8s → 16s → 30s (cap at 30s)
  - Log each retry attempt at Warning level with the exception message
  - Log success at Information level with count of loaded automations
```

**NuGet:** `Polly` or `Microsoft.Extensions.Http.Resilience` (the newer .NET 8+ built-in resilience pipeline, which wraps Polly internally). Prefer `Microsoft.Extensions.Http.Resilience` since it integrates cleanly with `IHttpClientFactory`.

**`AutomationCacheInitializer` change:** Replace the current try/catch/throw with a `ResiliencePipeline` configured on the `IAutomationApiClient`'s `HttpClient` registration.

### Files to Modify

- `FlowForge.JobAutomator/Initialization/AutomationCacheInitializer.cs` — replace throw with retry loop
- `FlowForge.JobAutomator/Program.cs` — configure resilience pipeline on `IAutomationApiClient`
- `FlowForge.JobAutomator.csproj` — add `Microsoft.Extensions.Http.Resilience`

---

## Not Planned (Yet)

| Item | Reason deferred |
|---|---|
| Unit & integration test suite | Will tackle after all features above are implemented |
| Auth / OAuth2 | Explicitly out of scope (see AGENTS.md) |
| Multi-tenancy | Explicitly out of scope (see AGENTS.md) |
| Frontend UI | Explicitly out of scope (see AGENTS.md) |
| Reusable named custom trigger definitions | Explicitly out of scope (see AGENTS.md) |
