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
| Containerisation | Docker Compose (full stack: infrastructure + all application services) |
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
│   │   │   │   ├── JobStatus.cs
│   │   │   │   └── ConfigFieldType.cs
│   │   │   ├── Exceptions/
│   │   │   │   ├── DomainException.cs
│   │   │   │   ├── AutomationNotFoundException.cs
│   │   │   │   ├── InvalidAutomationException.cs
│   │   │   │   └── InvalidJobTransitionException.cs
│   │   │   ├── Repositories/
│   │   │   │   ├── IAutomationRepository.cs
│   │   │   │   ├── IJobRepository.cs
│   │   │   │   ├── IHostGroupRepository.cs
│   │   │   │   └── IWorkflowHostRepository.cs
│   │   │   ├── Tasks/
│   │   │   │   ├── ITaskTypeDescriptor.cs
│   │   │   │   ├── TaskParameterField.cs
│   │   │   │   └── ITaskTypeRegistry.cs
│   │   │   └── Triggers/
│   │   │       ├── ITriggerTypeDescriptor.cs
│   │   │       ├── ITriggerTypeRegistry.cs
│   │   │       ├── TriggerConfigSchema.cs
│   │   │       └── TriggerTypes.cs
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
│   │       ├── Tasks/
│   │       │   ├── TaskTypeRegistry.cs
│   │       │   └── Descriptors/
│   │       │       ├── SendEmailTaskDescriptor.cs
│   │       │       ├── HttpRequestTaskDescriptor.cs
│   │       │       └── RunScriptTaskDescriptor.cs
│   │       ├── Triggers/
│   │       │   ├── TriggerTypeRegistry.cs
│   │       │   └── Descriptors/        ← 5 built-in trigger descriptors
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
│       │   │   ├── TaskTypesController.cs
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
│       │   ├── Options/
│       │   │   ├── HeartbeatMonitorOptions.cs
│       │   │   └── PendingJobScannerOptions.cs
│       │   └── Workers/
│       │       ├── JobDispatcherWorker.cs
│       │       ├── HeartbeatMonitorWorker.cs
│       │       └── PendingJobScannerWorker.cs
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
│           ├── Options/
│           │   └── SmtpOptions.cs
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
        ├── compose.yaml              ← full stack (postgres ×4, redis, jaeger + all 5 app services)
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
TaskConfig     : string? (jsonb)   ← flat JSON handler parameters; snapshotted onto Job at creation
HostGroupId    : Guid
IsEnabled      : bool
ActiveJobId    : Guid?             ← set when a job is running; null when idle
TimeoutSeconds : int?              ← null = no timeout
MaxRetries     : int               ← 0 = no retry (default)
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
TaskConfig     : string? (jsonb)   ← snapshot of Automation.TaskConfig at creation; immutable
OutputJson     : string? (jsonb)   ← serialized context.Outputs written by WorkflowEngine on completion
TriggeredAt    : DateTimeOffset?
TimeoutSeconds : int?              ← copied from Automation at job creation
RetryAttempt   : int               ← 0 = first attempt
MaxRetries     : int               ← copied from Automation at job creation
CreatedAt      : DateTimeOffset
UpdatedAt      : DateTimeOffset
```

### TriggerConditionNode (stored as jsonb in Automation.ConditionRoot)
```json
// Leaf:
{ "operator": null, "triggerName": "daily-schedule", "nodes": null }

// Composite:
{ "operator": "And"|"Or", "triggerName": null, "nodes": [ ...nodes ] }
```

### JobStatus Lifecycle
```
Pending → Started → InProgress → Completed
                              ↘ Error
                              ↘ CompletedUnsuccessfully
                              ↘ Cancelled
```
Terminal statuses: `Completed`, `Error`, `CompletedUnsuccessfully`, `Cancelled`, `Removed`.

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
    int? TimeoutSeconds = null, int MaxRetries = 0, int RetryAttempt = 0,
    string? TaskConfig = null);

record AutomationChangedEvent(
    Guid AutomationId, ChangeType ChangeType, AutomationSnapshot? Snapshot);

record AutomationSnapshot(
    Guid Id, string Name, bool IsEnabled, Guid HostGroupId, string ConnectionId,
    string TaskId, IReadOnlyList<TriggerSnapshot> Triggers, TriggerConditionNode ConditionRoot,
    int? TimeoutSeconds = null, int MaxRetries = 0, string? TaskConfig = null);

record JobCreatedEvent(
    Guid JobId, string ConnectionId, Guid AutomationId, Guid HostGroupId,
    DateTimeOffset CreatedAt, int? TimeoutSeconds = null);

record JobAssignedEvent(
    Guid JobId, string ConnectionId, Guid HostId, Guid AutomationId, DateTimeOffset AssignedAt);

record JobStatusChangedEvent(
    Guid JobId, Guid AutomationId, string ConnectionId, JobStatus Status,
    string? Message, DateTimeOffset UpdatedAt, string? OutputJson = null);

record JobCancelRequestedEvent(Guid JobId, Guid HostId, DateTimeOffset RequestedAt);
```

---

## Task Type Discovery

Task types are self-describing via `ITaskTypeDescriptor` (in `FlowForge.Domain/Tasks/`). The registry is built in Infrastructure and registered by `AddInfrastructure()`.

```csharp
public interface ITaskTypeDescriptor
{
    string TaskId { get; }
    string DisplayName { get; }
    string? Description { get; }
    IReadOnlyList<TaskParameterField> Parameters { get; }
}

public record TaskParameterField(
    string Name, string Label, string Type,  // "text", "textarea", "number", "boolean"
    bool Required, string? DefaultValue = null, string? HelpText = null);
```

Built-in descriptors: `SendEmailTaskDescriptor`, `HttpRequestTaskDescriptor`, `RunScriptTaskDescriptor`.

Endpoint: `GET /api/task-types` · `GET /api/task-types/{taskId}`

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

## Docker Compose

`deploy/docker/compose.yaml` runs the full stack:

**Infrastructure:**
- `flowforge-db-platform` — PostgreSQL 17 on port 5432
- `flowforge-db-minion` — PostgreSQL 17 on port 5433
- `flowforge-db-titan` — PostgreSQL 17 on port 5434
- `flowforge-db-quartz` — PostgreSQL 17 on port 5435 (initialised with `quartz-postgresql.sql`)
- `flowforge-redis` — Redis 7 with AOF persistence on port 6379
- `flowforge-jaeger` — Jaeger all-in-one on ports 16686 (UI) and 4317 (OTLP gRPC)

**Application services:**
- `flowforge-webapi` — port 8080, depends on platform DB + both job DBs + Redis
- `flowforge-job-automator` — port 8081, depends on WebApi healthy
- `flowforge-job-orchestrator` — port 8092, depends on platform DB + both job DBs + Redis
- `flowforge-workflowhost-minion` — port 8083, `NODE_NAME=minion`
- `flowforge-workflowhost-titan` — port 8084, `NODE_NAME=titan`
