# SPECS.md — Solution Structure

## Tech Stack

| Component | Choice |
|---|---|
| Runtime | .NET 10 |
| Web Framework | ASP.NET Core 10 |
| Messaging | Redis Streams (StackExchange.Redis) |
| Database | PostgreSQL (Npgsql + EF Core 10) |
| Real-time Push | SignalR |
| Scheduler | Quartz.NET 3.x (clustered, PostgreSQL store) |
| Observability | OpenTelemetry (tracing + metrics), Jaeger |
| Containerisation | Docker Compose (infrastructure only; application services — see ROADMAP #2) |
| Testing | xUnit, FluentAssertions, NSubstitute, Testcontainers |

---

## Solution Layout

```
FlowForge.sln
├── src/
│   ├── shared/
│   │   ├── FlowForge.Domain/
│   │   │   ├── Entities/
│   │   │   │   ├── Automation.cs
│   │   │   │   ├── Trigger.cs
│   │   │   │   ├── TriggerConditionNode.cs
│   │   │   │   ├── Job.cs
│   │   │   │   ├── HostGroup.cs
│   │   │   │   ├── WorkflowHost.cs
│   │   │   │   └── OutboxMessage.cs
│   │   │   ├── Enums/
│   │   │   │   └── JobStatus.cs
│   │   │   ├── Exceptions/
│   │   │   │   ├── DomainException.cs
│   │   │   │   ├── AutomationNotFoundException.cs
│   │   │   │   ├── InvalidAutomationException.cs
│   │   │   │   └── InvalidJobTransitionException.cs
│   │   │   └── Repositories/
│   │   │       ├── IAutomationRepository.cs
│   │   │       ├── IJobRepository.cs
│   │   │       ├── IHostGroupRepository.cs
│   │   │       └── IWorkflowHostRepository.cs
│   │   │
│   │   ├── FlowForge.Contracts/
│   │   │   └── Events/
│   │   │       ├── AutomationTriggeredEvent.cs
│   │   │       ├── AutomationChangedEvent.cs
│   │   │       ├── JobCreatedEvent.cs
│   │   │       ├── JobAssignedEvent.cs
│   │   │       ├── JobStatusChangedEvent.cs
│   │   │       └── JobCancelRequestedEvent.cs
│   │   │
│   │   └── FlowForge.Infrastructure/
│   │       ├── Caching/
│   │       │   ├── IRedisService.cs
│   │       │   └── RedisService.cs
│   │       ├── Messaging/
│   │       │   ├── Abstractions/
│   │       │   │   ├── IMessagePublisher.cs
│   │       │   │   ├── IMessageConsumer.cs
│   │       │   │   └── IStreamBootstrapper.cs
│   │       │   ├── DeadLetter/
│   │       │   │   ├── IDlqWriter.cs
│   │       │   │   └── DlqWriter.cs
│   │       │   ├── Outbox/
│   │       │   │   └── IOutboxWriter.cs
│   │       │   └── Redis/
│   │       │       ├── RedisStreamPublisher.cs
│   │       │       ├── RedisStreamConsumer.cs
│   │       │       ├── RedisStreamBootstrapper.cs
│   │       │       └── StreamNames.cs
│   │       ├── Persistence/
│   │       │   ├── Platform/           ← Automations, Triggers, HostGroups, WorkflowHosts, OutboxMessages
│   │       │   │   ├── PlatformDbContext.cs
│   │       │   │   ├── Configurations/
│   │       │   │   └── Migrations/
│   │       │   └── Jobs/               ← Jobs (one context, N databases)
│   │       │       ├── JobsDbContext.cs
│   │       │       ├── Configurations/
│   │       │       └── Migrations/
│   │       └── Telemetry/
│   │           ├── TelemetryExtensions.cs
│   │           ├── FlowForgeActivitySources.cs
│   │           └── FlowForgeMetrics.cs
│   │
│   └── services/
│       ├── FlowForge.WebApi/
│       │   ├── Controllers/
│       │   │   ├── AutomationsController.cs
│       │   │   ├── JobsController.cs
│       │   │   ├── TriggersController.cs
│       │   │   ├── HostGroupsController.cs
│       │   │   └── DlqController.cs
│       │   ├── DTOs/Requests/ & DTOs/Responses/
│       │   ├── Hubs/JobStatusHub.cs
│       │   ├── Middleware/ExceptionHandlingMiddleware.cs
│       │   ├── Options/OutboxRelayOptions.cs
│       │   ├── Services/
│       │   │   ├── IAutomationService.cs / AutomationService.cs
│       │   │   └── IJobService.cs / JobService.cs
│       │   └── Workers/
│       │       ├── AutomationTriggeredConsumer.cs
│       │       ├── JobStatusChangedConsumer.cs
│       │       └── OutboxRelayWorker.cs
│       │
│       ├── FlowForge.JobAutomator/
│       │   ├── Cache/AutomationCache.cs
│       │   ├── Clients/IAutomationApiClient.cs
│       │   ├── Evaluators/  (ITriggerEvaluator + 5 implementations)
│       │   ├── Initialization/AutomationCacheInitializer.cs
│       │   ├── Options/AutomationWorkerOptions.cs
│       │   └── Workers/
│       │       ├── AutomationWorker.cs
│       │       ├── AutomationCacheSyncWorker.cs
│       │       └── JobCompletedFlagWorker.cs
│       │
│       ├── FlowForge.JobOrchestrator/
│       │   ├── LoadBalancing/
│       │   │   ├── ILoadBalancer.cs
│       │   │   └── RoundRobinLoadBalancer.cs
│       │   ├── Options/HeartbeatMonitorOptions.cs
│       │   └── Workers/
│       │       ├── JobDispatcherWorker.cs
│       │       └── HeartbeatMonitorWorker.cs
│       │
│       ├── FlowForge.WorkflowHost/
│       │   ├── Options/HostHeartbeatOptions.cs
│       │   ├── ProcessManagement/
│       │   │   ├── IProcessManager.cs
│       │   │   ├── NativeProcessManager.cs
│       │   │   └── DockerProcessManager.cs
│       │   └── Workers/
│       │       ├── JobConsumerWorker.cs
│       │       ├── CancelConsumerWorker.cs
│       │       └── HostHeartbeatWorker.cs
│       │
│       └── FlowForge.WorkflowEngine/
│           ├── Handlers/
│           │   ├── IWorkflowHandler.cs
│           │   ├── WorkflowModels.cs        ← WorkflowContext, WorkflowResult
│           │   ├── WorkflowHandlerRegistry.cs
│           │   ├── SendEmailHandler.cs
│           │   ├── HttpRequestHandler.cs
│           │   └── RunScriptHandler.cs
│           ├── Reporting/
│           │   └── JobProgressReporter.cs
│           └── Program.cs
│
├── tests/
│   ├── FlowForge.Domain.Tests/
│   │   ├── TriggerConditionEvaluatorTests.cs
│   │   ├── JobStateMachineTests.cs
│   │   └── AutomationInvariantsTests.cs
│   └── FlowForge.Integration.Tests/
│       ├── Infrastructure/           ← shared Testcontainers fixtures
│       ├── Workers/
│       │   ├── AutomationTriggeredConsumerTests.cs
│       │   ├── JobStatusChangedConsumerTests.cs
│       │   └── OutboxRelayWorkerTests.cs
│       └── Services/
│           └── AutomationServiceTests.cs
│
└── deploy/
    └── docker/
        ├── compose.yaml              ← infrastructure only (postgres ×4, redis, jaeger)
        └── quartz-postgresql.sql
```

---

## Domain Entities

### Automation
```
Id             : Guid
Name           : string (max 200)
Description    : string?
TaskId         : string
HostGroupId    : Guid
IsEnabled      : bool
ActiveJobId    : Guid?          ← set when a job is running; null when idle
TimeoutSeconds : int?           ← null = no timeout
MaxRetries     : int            ← 0 = no retry (default)
ConditionRoot  : string (jsonb)
Triggers       : IList<Trigger>
CreatedAt      : DateTimeOffset
UpdatedAt      : DateTimeOffset
```

### Trigger
```
Id          : Guid
AutomationId: Guid (FK)
Name        : string (max 100)   ← referenced by ConditionRoot leaf nodes
TypeId      : string             ← "schedule", "sql", "webhook", "job-completed", "custom-script"
ConfigJson  : string (jsonb)
CreatedAt   : DateTimeOffset
UpdatedAt   : DateTimeOffset
```

### Job
```
Id             : Guid
AutomationId   : Guid
TaskId         : string
ConnectionId   : string
HostGroupId    : Guid
HostId         : Guid?
Status         : JobStatus
Message        : string? (max 1000)
TriggeredAt    : DateTimeOffset?
TimeoutSeconds : int?           ← copied from Automation at job creation
RetryAttempt   : int            ← 0 = first attempt
MaxRetries     : int            ← copied from Automation at job creation
CreatedAt      : DateTimeOffset
UpdatedAt      : DateTimeOffset
```

### TriggerConditionNode (stored as jsonb in Automation.ConditionRoot)
```json
// Leaf:
{ "type": "trigger", "name": "daily-schedule" }

// Composite:
{ "type": "and"|"or", "children": [ ...nodes ] }
```

### JobStatus Lifecycle
```
Pending → Started → InProgress → Completed
                              ↘ Error
                              ↘ CompletedUnsuccessfully
                              ↘ Cancelled
```
Terminal statuses: `Completed`, `Error`, `CompletedUnsuccessfully`, `Cancelled`.

---

## Static TriggerTypes
```csharp
public static class TriggerTypes
{
    public const string Schedule     = "schedule";
    public const string Sql          = "sql";
    public const string JobCompleted = "job-completed";
    public const string Webhook      = "webhook";
    public const string CustomScript = "custom-script";
}
```

---

## Domain Exceptions

| Exception | Thrown when |
|---|---|
| `AutomationNotFoundException` | `GetByIdAsync` returns null in a consumer |
| `InvalidAutomationException` | Validation fails in `Automation.Create` / `Update` |
| `InvalidJobTransitionException` | `Job.Transition` called with an invalid state change |

---

## Event Contracts (`FlowForge.Contracts`)

```csharp
record AutomationTriggeredEvent(
    Guid AutomationId, Guid HostGroupId, string ConnectionId, string TaskId,
    DateTimeOffset TriggeredAt,
    int? TimeoutSeconds = null, int MaxRetries = 0, int RetryAttempt = 0);

record AutomationChangedEvent(
    Guid AutomationId, ChangeType ChangeType, AutomationSnapshot? Snapshot);

record AutomationSnapshot(
    Guid Id, string Name, bool IsEnabled, Guid HostGroupId, string ConnectionId,
    string TaskId, IReadOnlyList<TriggerSnapshot> Triggers, TriggerConditionNode ConditionRoot,
    int? TimeoutSeconds = null, int MaxRetries = 0);

record JobCreatedEvent(
    Guid JobId, string ConnectionId, Guid AutomationId, Guid HostGroupId,
    DateTimeOffset CreatedAt, int? TimeoutSeconds = null);

record JobAssignedEvent(
    Guid JobId, string ConnectionId, Guid HostId, Guid AutomationId, DateTimeOffset AssignedAt);

record JobStatusChangedEvent(
    Guid JobId, Guid AutomationId, string ConnectionId, JobStatus Status,
    string? Message, DateTimeOffset UpdatedAt);

record JobCancelRequestedEvent(Guid JobId, Guid HostId, DateTimeOffset RequestedAt);
```

---

## Key NuGet Packages

| Package | Used in |
|---|---|
| `Microsoft.EntityFrameworkCore` + `Npgsql.EF` | Infrastructure |
| `StackExchange.Redis` | Infrastructure |
| `OpenTelemetry.*` + `Instrumentation.Runtime` | Infrastructure |
| `Quartz` + `Quartz.Extensions.DependencyInjection` | JobAutomator |
| `AspNetCore.HealthChecks.NpgsqlServer` + `.Redis` | All services |
| `FluentValidation.AspNetCore` | WebApi |
| `Microsoft.AspNetCore.SignalR` | WebApi |
| `xunit` + `FluentAssertions` + `NSubstitute` | Tests |
| `Testcontainers.PostgreSql` + `Testcontainers.Redis` | Integration tests |

---

## Multi-Database Architecture

```
Platform DB   (flowforge_platform) — Automations, Triggers, HostGroups, WorkflowHosts, OutboxMessages
Quartz DB     (flowforge_quartz)   — Quartz.NET scheduler state
Job DB minion (flowforge_minion)   — Jobs for host group "wf-jobs-minion"
Job DB titan  (flowforge_titan)    — Jobs for host group "wf-jobs-titan"
```

Job DBs are registered as keyed `IJobRepository` services using the `ConnectionId` as the key. New host groups require a new PostgreSQL database, a new connection string in `JobConnections` config, and a migration applied to the new database.

---

## Infrastructure Docker Compose

`deploy/docker/compose.yaml` runs:
- `flowforge-db-platform` — PostgreSQL 16 on port 5432
- `flowforge-db-minion` — PostgreSQL 16 on port 5433
- `flowforge-db-titan` — PostgreSQL 16 on port 5434
- `flowforge-db-quartz` — PostgreSQL 16 on port 5435 (initialised with `quartz-postgresql.sql`)
- `flowforge-redis` — Redis 7 with AOF persistence on port 6379
- `flowforge-jaeger` — Jaeger all-in-one on ports 16686 (UI) and 4317 (OTLP gRPC)

Application services are not yet containerised — see ROADMAP item #2.
