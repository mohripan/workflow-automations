# AGENTS.md — FlowForge Architecture Overview

FlowForge is a .NET 10 workflow orchestration platform. It evaluates automation trigger conditions on a schedule, creates jobs when conditions are met, routes them to the appropriate worker hosts, executes them as isolated child processes, and reports outcomes back — all over Redis Streams.

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
- **AutomationTriggeredConsumer** — creates jobs, prevents duplicates via `ActiveJobId` lock, passes retry context
- **JobStatusChangedConsumer** — updates job state, clears the automation lock on terminal status, schedules retries via outbox when `RetryAttempt < MaxRetries`
- **OutboxRelayWorker** — polls `OutboxMessages` every `OutboxRelayOptions.PollIntervalMs` ms and publishes to Redis Streams

Also hosts `DlqController` for operator inspection, replay, and deletion of dead-letter entries.

Exposes health probes at `/health/live` and `/health/ready`.

### JobAutomator (`FlowForge.JobAutomator`)
Evaluates trigger conditions for all enabled automations using an in-memory cache. When the condition tree evaluates to `true` it publishes `AutomationTriggeredEvent`. Uses Quartz.NET (clustered, PostgreSQL-backed) for schedule-based triggers. Evaluation runs every `AutomationWorkerOptions.EvaluationIntervalSeconds` seconds.

Exposes health probes at `/health/live` and `/health/ready`.

### JobOrchestrator (`FlowForge.JobOrchestrator`)
Consumes `JobCreatedEvent`, selects an online host via round-robin load balancing, transitions the job to `Started`, and publishes `JobAssignedEvent` to the host-specific Redis Stream. Also monitors host heartbeats every `HeartbeatMonitorOptions.CheckIntervalSeconds` seconds and marks unresponsive hosts offline.

Exposes health probes at `/health/live` and `/health/ready`.

### WorkflowHost (`FlowForge.WorkflowHost`)
Runs as a Kubernetes DaemonSet (one replica per node). Consumes the host-specific `JobAssignedEvent` stream and spawns one `WorkflowEngine` process per job via fire-and-forget. Maintains a Redis heartbeat key so JobOrchestrator knows the host is alive. Configurable heartbeat interval via `HostHeartbeatOptions`.

Exposes health probes at `/health/live` and `/health/ready`.

### WorkflowEngine (`FlowForge.WorkflowEngine`)
Short-lived console application spawned by WorkflowHost. Receives `JOB_ID`, `JOB_AUTOMATION_ID`, and `CONNECTION_ID` via environment variables. Resolves the job's `TaskId` to a registered `IWorkflowHandler`, executes it, and publishes the final `JobStatusChangedEvent`. Enforces optional per-job timeouts via a linked `CancellationTokenSource`. Records `flowforge.jobs.duration_seconds` on completion.

---

## Core Concepts

### Automation
The primary configuration unit. Specifies:
- **TaskId** — which workflow handler to execute (e.g. `"send-email"`, `"http-request"`)
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
- **TimeoutSeconds** — enforced by WorkflowEngine's timeout CTS (copied from Automation at job creation time)
- **RetryAttempt** — which attempt this is (0 = first)
- **MaxRetries** — upper bound on retries (copied from Automation at job creation time)

### Host Group
A logical pool of WorkflowHost instances sharing a job database. Each host group has a `ConnectionId` that maps to a named connection string in `JobConnections` configuration.

### TaskId
A string identifier (`"send-email"`, `"http-request"`, `"run-script"`) that maps to an `IWorkflowHandler` registered in WorkflowEngine. The handler performs the actual work.

> **Note:** Currently `WorkflowContext.Parameters` is always an empty dictionary, so handlers cannot receive per-automation configuration. This is ROADMAP item #1.

---

## Communication Patterns

All inter-service communication uses **Redis Streams** with consumer groups.

| Stream | Publisher | Consumer(s) |
|---|---|---|
| `flowforge:automation-triggered` | JobAutomator / WebApi (retry outbox) | WebApi `AutomationTriggeredConsumer` |
| `flowforge:automation-changed` | WebApi (outbox) | JobAutomator `AutomationCacheSyncWorker` |
| `flowforge:job-created` | WebApi (outbox) | JobOrchestrator `JobDispatcherWorker` |
| `flowforge:host:{hostId}` | JobOrchestrator | WorkflowHost `JobConsumerWorker` |
| `flowforge:job-status-changed` | WorkflowEngine | WebApi `JobStatusChangedConsumer`, JobAutomator `JobCompletedFlagWorker` |
| `flowforge:job-cancel-requested` | WebApi | WorkflowHost `CancelConsumerWorker` |
| `flowforge:dlq` | All consumers (on error) | `DlqController` (operator) |

The **Transactional Outbox** pattern is used for all writes that must be atomic with a database mutation. `IOutboxWriter` appends to `OutboxMessages` in the same `SaveChangesAsync` call; `OutboxRelayWorker` polls and publishes to Redis.

---

## Reliability Features

| Feature | Status | Implementation |
|---|---|---|
| Dead Letter Queue | ✅ | Failed messages written to `flowforge:dlq`; XACK always in `finally` |
| Job Auto-Retry | ✅ | `JobStatusChangedConsumer` re-queues with `RetryAttempt+1` when `RetryAttempt < MaxRetries` |
| Job Timeout | ✅ | WorkflowEngine creates a linked CTS from `job.TimeoutSeconds` |
| Duplicate Job Prevention | ✅ | `automation.ActiveJobId` set on creation; checked before creating a new job |
| Heartbeat Monitoring | ✅ | WorkflowHost publishes Redis key with TTL; JobOrchestrator marks offline on expiry |
| Health Probes | ✅ | All services: `/health/live` (liveness) and `/health/ready` (readiness) |
| Configurable Intervals | ✅ | `IOptions<T>` options classes per worker; defaults in `appsettings.json` |

---

## Observability

- **Distributed Tracing** — OpenTelemetry + OTLP exporter (Jaeger in dev). Trace context propagated across Redis Streams via `traceparent` field on every message.
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
│   │   ├── FlowForge.Domain          # Entities, aggregates, repositories, exceptions
│   │   ├── FlowForge.Contracts       # Event records (shared across services)
│   │   └── FlowForge.Infrastructure  # EF Core, Redis, Telemetry, Messaging, DLQ
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
# Start infrastructure (PostgreSQL ×4, Redis, Jaeger)
docker compose -f deploy/docker/compose.yaml up -d

# Restore and build
dotnet restore FlowForge.sln && dotnet build FlowForge.sln

# Apply platform DB migration (job DBs auto-migrate on WebApi startup)
dotnet ef database update \
  --project src/shared/FlowForge.Infrastructure \
  --startup-project src/services/FlowForge.WebApi \
  --context PlatformDbContext

# Run tests
dotnet test FlowForge.sln
```

Start each service with `dotnet run` from its project directory, or use your IDE.

---

## Known Limitations

| Limitation | Roadmap |
|---|---|
| `WorkflowContext.Parameters` is always an empty dictionary — handlers cannot receive per-automation config | Item #1 |
| JobDispatcherWorker silently drops jobs when no hosts are online | Item #4 |
| Application services are not containerised — only infrastructure runs in Docker | Item #2 |
