# AGENTS.md — FlowForge Architecture Overview

FlowForge is a .NET 10 workflow orchestration platform. It evaluates automation trigger conditions on a schedule, creates jobs when conditions are met, routes them to the appropriate worker hosts, executes them as isolated child processes, and reports outcomes back — all over a provider-agnostic messaging layer (Redis Streams or Dapr+Kafka).

---

## High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                          External World                              │
│  REST API clients  •  SignalR UI clients  •  Webhook senders        │
└───────────────────────────────┬─────────────────────────────────────┘
                                │
                ┌───────────────▼────────────────┐
                │            WebApi              │
                │   REST + SignalR + Workers     │
                └────┬──────────────────────┬────┘
                     │                      │
         Platform DB │         Redis Streams│
         (PostgreSQL) │                      │
                     │                      │
    ┌────────────────▼──────────┐  ┌────────▼──────────────┐
    │       JobAutomator        │  │    JobOrchestrator     │
    │   Trigger Evaluation      │  │  Dispatch + Heartbeat  │
    └───────────────────────────┘  └──────────┬─────────────┘
                                              │
                                 ┌────────────▼────────────┐
                                 │      WorkflowHost        │
                                 │  DaemonSet per node      │
                                 └────────────┬─────────────┘
                                              │ spawn
                                 ┌────────────▼────────────┐
                                 │     WorkflowEngine       │
                                 │  One process per job     │
                                 └─────────────────────────┘
```

---

## Services

### WebApi (`FlowForge.WebApi`)
ASP.NET Core web application. Handles the REST API, SignalR real-time push, and three background workers:
- **AutomationTriggeredConsumer** — creates jobs, prevents duplicates via `ActiveJobId` lock, passes retry context and `TaskConfig` snapshot
- **JobStatusChangedConsumer** — updates job state, persists `OutputJson` on completion, clears the automation lock on terminal status, schedules retries via outbox when `RetryAttempt < MaxRetries`
- **OutboxRelayWorker** — polls `OutboxMessages` every `OutboxRelayOptions.PollIntervalMs` ms and publishes via `IMessagePublisher` (works with both Redis Streams and Dapr+Kafka)

Also hosts `DlqController` for operator inspection, replay, and deletion of dead-letter entries.
Exposes task type discovery at `GET /api/task-types`.
Exposes health probes at `/health/live` and `/health/ready`.

### JobAutomator (`FlowForge.JobAutomator`)
Evaluates trigger conditions for all enabled automations using an in-memory cache. When the condition tree evaluates to `true` it publishes `AutomationTriggeredEvent` (including the automation's `TaskConfig`). Uses Quartz.NET (clustered, PostgreSQL-backed) for schedule-based triggers. Evaluation runs every `AutomationWorkerOptions.EvaluationIntervalSeconds` seconds.

Exposes health probes at `/health/live` and `/health/ready`.

### JobOrchestrator (`FlowForge.JobOrchestrator`)
Consumes `JobCreatedEvent`, selects an online host via round-robin load balancing, transitions the job to `Started`, and publishes `JobAssignedEvent` to `flowforge:host:{hostName}`. Also monitors host heartbeats every `HeartbeatMonitorOptions.CheckIntervalSeconds` seconds and marks unresponsive hosts offline. A `PendingJobScannerWorker` periodically re-queues jobs stuck in `Pending` (stale after `PendingJobScannerOptions.StaleAfterSeconds`) so they are dispatched once hosts come back online.

Exposes health probes at `/health/live` and `/health/ready`.

### WorkflowHost (`FlowForge.WorkflowHost`)
Runs as a Kubernetes DaemonSet (one replica per node). Consumes the host-specific `JobAssignedEvent` stream and spawns one `WorkflowEngine` process per job via fire-and-forget. Maintains a Redis heartbeat key so JobOrchestrator knows the host is alive. Configurable heartbeat interval via `HostHeartbeatOptions`. `NativeProcessManager` detects whether the engine path is a `.dll` and launches it via `dotnet <dll>` accordingly.

Exposes health probes at `/health/live` and `/health/ready`.

### WorkflowEngine (`FlowForge.WorkflowEngine`)
Short-lived console application spawned by WorkflowHost. Receives `JOB_ID`, `JOB_AUTOMATION_ID`, and `CONNECTION_ID` via environment variables. Deserializes `job.TaskConfig` into `WorkflowContext.Parameters` so handlers receive their per-automation configuration. Resolves the job's `TaskId` to a registered `IWorkflowHandler`, executes it, serializes `context.Outputs` into `OutputJson`, and publishes the final `JobStatusChangedEvent` (with `OutputJson`). Enforces optional per-job timeouts via a linked `CancellationTokenSource`. Records `flowforge.jobs.duration_seconds` on completion.

---

## Core Concepts

### Automation
The primary configuration unit. Specifies:
- **TaskId** — which workflow handler to execute (e.g. `"send-email"`, `"http-request"`)
- **TaskConfig** — JSON blob of handler parameters (e.g. `{"to":"...", "subject":"..."}`) snapshotted onto each Job at creation
- **HostGroupId** — which pool of hosts may run jobs for this automation
- **Triggers** — one or more named conditions
- **ConditionRoot** — AND/OR tree combining trigger names
- **IsEnabled** — whether the automation participates in evaluation
- **TimeoutSeconds** — optional per-job execution timeout (null = unlimited)
- **MaxRetries** — how many times a failed job is automatically retried (0 = no retry)

### Trigger
A named condition attached to an automation. Has a `TypeId` string (e.g. `"schedule"`, `"sql"`, `"webhook"`) and a `ConfigJson` blob specific to that type. Evaluated by a matching `ITriggerEvaluator` in the JobAutomator.

### Trigger Condition Tree
A recursive AND/OR tree stored as `ConditionRoot` on the Automation. The evaluator resolves each leaf to the boolean result of the named trigger; the root value determines whether the automation fires.

### Job
Created when an automation fires. Carries:
- **Status** — state machine: `Pending → Started → InProgress → Completed / Error / CompletedUnsuccessfully / Cancelled`
- **TaskId** — what the engine should execute
- **TaskConfig** — snapshot of handler parameters at creation time (immutable after creation)
- **OutputJson** — serialized `context.Outputs` written back by the engine on completion (jsonb, nullable)
- **TimeoutSeconds** — enforced by WorkflowEngine's timeout CTS (copied from Automation at job creation)
- **RetryAttempt** — which attempt this is (0 = first)
- **MaxRetries** — upper bound on retries (copied from Automation at job creation)

### Host Group
A logical pool of WorkflowHost instances sharing a job database. Each host group has a `ConnectionId` that maps to a named connection string in `JobConnections` configuration.

### TaskId
A string identifier (`"send-email"`, `"http-request"`, `"run-script"`) that maps to an `IWorkflowHandler` registered in WorkflowEngine and an `ITaskTypeDescriptor` registered in Infrastructure (discoverable via `GET /api/task-types`).

---

## Communication Patterns

### Messaging Layer

All inter-service communication uses a **provider-agnostic messaging layer**. Two providers are supported:

| Provider | Selection | Transport | How consumers receive events |
|---|---|---|---|
| **Redis Streams** (default) | `Messaging:Provider = "redis"` | Redis Streams with consumer groups | `BackgroundService` workers pull from streams (existing behavior) |
| **Dapr+Kafka** | `Messaging:Provider = "dapr"` | Kafka via Dapr pub/sub component | Dapr sidecars push to HTTP subscription endpoints (`/dapr/{topicName}`) |

Provider selection is configuration-driven via `Messaging:Provider` in `appsettings.json`.

**Key abstractions** (in `FlowForge.Infrastructure`):

| Abstraction | Role |
|---|---|
| `IMessagePublisher` | Publish events to the configured transport |
| `IEventHandler<TEvent>` | Business logic for handling a specific event type (transport-independent) |
| `IMessagingInfrastructure` | Registers consumers/subscriptions at startup for the active provider |
| `IDlqReader` / `IDlqWriter` | Dead-letter queue access — works identically in both modes |

Business logic lives in `IEventHandler<TEvent>` implementations, which are completely independent of the underlying transport. Workers (Redis mode) and subscription endpoints (Dapr mode) are thin shells that delegate to the same handlers.

### Topics

| Topic | Publisher | Consumer(s) |
|---|---|---|
| `flowforge:automation-triggered` | JobAutomator / WebApi (retry outbox) | WebApi `AutomationTriggeredConsumer` |
| `flowforge:automation-changed` | WebApi (outbox) | JobAutomator `AutomationCacheSyncWorker` |
| `flowforge:job-created` | WebApi (outbox) | JobOrchestrator `JobDispatcherWorker`, `PendingJobScannerWorker` (re-queues) |
| `flowforge:host:{hostName}` | JobOrchestrator | WorkflowHost `JobConsumerWorker` |
| `flowforge:job-status-changed` | WorkflowEngine | WebApi `JobStatusChangedConsumer`, JobAutomator `JobCompletedFlagWorker` |
| `flowforge:job-cancel-requested` | WebApi | WorkflowHost `CancelConsumerWorker` |
| `flowforge:dlq` | All consumers (on error) | `DlqController` (operator) |

The host topic key uses the host's **name** (matching `NODE_NAME` env var), not its database GUID. Topic names are defined in the `TopicNames` constants class.

### Transactional Outbox

The **Transactional Outbox** pattern is used for all writes that must be atomic with a database mutation. `IOutboxWriter` appends to `OutboxMessages` in the same `SaveChangesAsync` call; `OutboxRelayWorker` polls and publishes via `IMessagePublisher` (which routes to the active provider).

### Dapr Infrastructure

When running with Dapr, use the extended compose file:
```bash
docker compose -f deploy/docker/compose.yaml -f deploy/docker/compose.dapr.yaml up -d
```
This adds Kafka (KRaft mode, no ZooKeeper) and Dapr sidecars for each service. Dapr pub/sub is configured to use Kafka as the backing broker.

---

## Reliability Features

| Feature | Status | Implementation |
|---|---|---|
| Dead Letter Queue | ✅ | Failed messages written to `flowforge:dlq`; XACK always in `finally` |
| Job Auto-Retry | ✅ | `JobStatusChangedConsumer` re-queues with `RetryAttempt+1` when `RetryAttempt < MaxRetries` |
| Job Timeout | ✅ | WorkflowEngine creates a linked CTS from `job.TimeoutSeconds` |
| Duplicate Job Prevention | ✅ | `automation.ActiveJobId` set on creation; checked before creating a new job |
| Heartbeat Monitoring | ✅ | WorkflowHost publishes Redis key with TTL; JobOrchestrator marks offline on expiry |
| No-Host Re-queuing | ✅ | `PendingJobScannerWorker` re-publishes stale `Pending` jobs every 30 s |
| Health Probes | ✅ | All services: `/health/live` (liveness) and `/health/ready` (readiness) |
| Configurable Intervals | ✅ | `IOptions<T>` options classes per worker; defaults in `appsettings.json` |

---

## Observability

- **Distributed Tracing** — OpenTelemetry + OTLP exporter (Jaeger in dev). Trace context propagated across messaging via `traceparent` field on every message (both Redis Streams and Dapr).
- **Metrics** — `FlowForgeMetrics` static `Meter` (`"FlowForge"`):
  - `flowforge.jobs.created` (counter, tag: `automation_id`, `host_group_id`)
  - `flowforge.jobs.completed` (counter, tag: `host_group_id`)
  - `flowforge.jobs.failed` (counter, tag: `host_group_id`)
  - `flowforge.triggers.fired` (counter, tag: `automation_id`)
  - `flowforge.jobs.duration_seconds` (histogram, tag: `task_id`)
- **Logging** — Structured `ILogger<T>` throughout; all workers log at `Debug`/`Information`/`Warning`/`Error` as appropriate.

---

## Repository Layout

```
FlowForge.sln
├── src/
│   ├── shared/
│   │   ├── FlowForge.Domain          # Entities, aggregates, repositories, exceptions, task type interfaces
│   │   ├── FlowForge.Contracts       # Event records (shared across services)
│   │   └── FlowForge.Infrastructure  # EF Core, Redis, Telemetry, Messaging, DLQ, task/trigger type registries
│   └── services/
│       ├── FlowForge.WebApi
│       ├── FlowForge.JobAutomator
│       ├── FlowForge.JobOrchestrator
│       ├── FlowForge.WorkflowHost
│       └── FlowForge.WorkflowEngine
├── tests/
│   ├── FlowForge.Domain.Tests        # Pure unit tests (no infrastructure)
│   └── FlowForge.Integration.Tests   # Testcontainers: PostgreSQL + Redis
└── docs/
```

---

## Development Setup

**Prerequisites:** Docker Desktop, .NET 10 SDK

```bash
# Start full stack (infrastructure + all application services)
docker compose -f deploy/docker/compose.yaml up -d

# Or run services locally — start infrastructure first
docker compose -f deploy/docker/compose.yaml up -d \
  postgres-platform postgres-minion postgres-titan postgres-quartz redis jaeger

# Apply platform DB migration
dotnet ef database update \
  --project src/shared/FlowForge.Infrastructure \
  --startup-project src/services/FlowForge.WebApi \
  --context PlatformDbContext

# Apply job DB migrations (minion and titan)
dotnet ef database update \
  --project src/shared/FlowForge.Infrastructure \
  --startup-project src/services/FlowForge.WebApi \
  --context JobsDbContext \
  --connection "Host=localhost;Port=5433;Database=flowforge_minion;Username=postgres;Password=postgres"

dotnet ef database update \
  --project src/shared/FlowForge.Infrastructure \
  --startup-project src/services/FlowForge.WebApi \
  --context JobsDbContext \
  --connection "Host=localhost;Port=5434;Database=flowforge_titan;Username=postgres;Password=postgres"

# Run tests
dotnet test FlowForge.sln
```
