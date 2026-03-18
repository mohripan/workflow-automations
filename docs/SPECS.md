# SPECS.md — Solution Structure

## Tech Stack

| Komponen | Pilihan |
|---|---|
| Runtime | **.NET 10** (C#) |
| Web Framework | ASP.NET Core 10 |
| Message Broker | Redis Streams |
| Database | PostgreSQL (EF Core 10) |
| Cache & Heartbeat | Redis |
| Real-time (frontend) | SignalR |
| Scheduler (trigger) | Quartz.NET |
| Deployment | Docker + Kubernetes |

---

## Solution Structure

```
FlowForge/
├── FlowForge.sln
├── AGENTS.md
├── SPECS.md
├── CONVENTIONS.md
├── TRIGGERS.md           ← trigger type system, custom-script, TriggersController
├── JOBAUTOMATOR.md
├── JOBORCHESTRATOR.md
├── WORKFLOWHOST.md
├── WORKFLOWENGINE.md
│
├── src/
│   │
│   ├── shared/
│   │   │
│   │   ├── FlowForge.Domain/
│   │   │   ├── Entities/
│   │   │   │   ├── Automation.cs                    # IsEnabled, ConditionRoot required
│   │   │   │   ├── Job.cs
│   │   │   │   ├── Trigger.cs                       # TypeId is string; Name unique within Automation
│   │   │   │   ├── WorkflowHost.cs
│   │   │   │   └── HostGroup.cs
│   │   │   ├── Triggers/
│   │   │   │   ├── TriggerTypes.cs                  # String constants: "schedule", "sql", etc.
│   │   │   │   ├── ITriggerTypeDescriptor.cs        # Self-describes a type + validates configJson
│   │   │   │   ├── ITriggerTypeRegistry.cs          # Lookup by TypeId; populated at startup
│   │   │   │   ├── TriggerConfigSchema.cs           # Schema DTO returned by TriggersController
│   │   │   │   └── ConfigField.cs                   # One field descriptor (name, label, dataType, ...)
│   │   │   ├── Enums/
│   │   │   │   ├── JobStatus.cs
│   │   │   │   ├── ConditionOperator.cs             # And | Or
│   │   │   │   └── ConfigFieldType.cs               # String | Int | Bool | CronExpression | Script | ...
│   │   │   ├── ValueObjects/
│   │   │   │   └── TriggerConditionNode.cs          # Recursive AND/OR tree; leaf uses TriggerName (string)
│   │   │   ├── Exceptions/
│   │   │   │   ├── DomainException.cs
│   │   │   │   ├── JobNotFoundException.cs
│   │   │   │   ├── AutomationNotFoundException.cs
│   │   │   │   ├── InvalidAutomationException.cs    # Empty triggers, null condition, unknown TypeId
│   │   │   │   ├── InvalidTriggerConditionException.cs
│   │   │   │   └── UnknownConnectionIdException.cs
│   │   │   └── FlowForge.Domain.csproj
│   │   │
│   │   ├── FlowForge.Contracts/
│   │   │   ├── Events/
│   │   │   │   ├── AutomationChangedEvent.cs
│   │   │   │   ├── AutomationTriggeredEvent.cs
│   │   │   │   ├── JobCreatedEvent.cs
│   │   │   │   ├── JobAssignedEvent.cs
│   │   │   │   ├── JobStatusChangedEvent.cs
│   │   │   │   └── JobCancelRequestedEvent.cs
│   │   │   └── FlowForge.Contracts.csproj
│   │   │
│   │   └── FlowForge.Infrastructure/
│   │       ├── Persistence/
│   │       │   ├── Platform/
│   │       │   │   ├── PlatformDbContext.cs
│   │       │   │   ├── Migrations/
│   │       │   │   └── Configurations/
│   │       │   │       ├── AutomationConfiguration.cs   # TriggerConditionNode as owned JSON column
│   │       │   │       ├── TriggerConfiguration.cs      # Unique index (AutomationId, Name); TypeId varchar(100)
│   │       │   │       ├── WorkflowHostConfiguration.cs
│   │       │   │       └── HostGroupConfiguration.cs
│   │       │   └── Jobs/
│   │       │       ├── JobsDbContext.cs
│   │       │       ├── Migrations/
│   │       │       └── Configurations/
│   │       │           └── JobConfiguration.cs
│   │       ├── Messaging/
│   │       │   ├── Abstractions/
│   │       │   │   ├── IMessagePublisher.cs
│   │       │   │   └── IMessageConsumer.cs
│   │       │   └── Redis/
│   │       │       ├── RedisStreamPublisher.cs
│   │       │       ├── RedisStreamConsumer.cs
│   │       │       └── StreamNames.cs
│   │       ├── Caching/
│   │       │   ├── IRedisService.cs
│   │       │   └── RedisService.cs
│   │       ├── Repositories/
│   │       │   ├── IJobRepository.cs
│   │       │   ├── IAutomationRepository.cs
│   │       │   └── IHostGroupRepository.cs
│   │       ├── MultiDb/
│   │       │   ├── JobsDbContextFactory.cs
│   │       │   └── ConnectionRegistry.cs
│   │       ├── Triggers/
│   │       │   ├── TriggerTypeRegistry.cs           # ITriggerTypeRegistry implementation
│   │       │   └── Descriptors/
│   │       │       ├── ScheduleTriggerDescriptor.cs
│   │       │       ├── SqlTriggerDescriptor.cs
│   │       │       ├── JobCompletedTriggerDescriptor.cs
│   │       │       ├── WebhookTriggerDescriptor.cs
│   │       │       └── CustomScriptTriggerDescriptor.cs
│   │       ├── ServiceCollectionExtensions.cs       # AddInfrastructure — registers all descriptors
│   │       └── FlowForge.Infrastructure.csproj
│   │
│   └── services/
│       │
│       ├── FlowForge.WebApi/
│       │   ├── Controllers/
│       │   │   ├── AutomationsController.cs         # enable/disable endpoints
│       │   │   ├── JobsController.cs
│       │   │   ├── TriggersController.cs            # GET types, GET type/{id}, POST validate-config
│       │   │   └── HostGroupsController.cs
│       │   ├── Hubs/
│       │   │   └── JobStatusHub.cs
│       │   ├── DTOs/
│       │   │   ├── Requests/
│       │   │   │   ├── CreateAutomationRequest.cs   # CreateTriggerRequest.TypeId is string
│       │   │   │   ├── UpdateAutomationRequest.cs
│       │   │   │   ├── ValidateConfigRequest.cs
│       │   │   │   └── CancelJobRequest.cs
│       │   │   └── Responses/
│       │   │       ├── AutomationResponse.cs        # TriggerResponse.TypeId is string
│       │   │       ├── JobResponse.cs
│       │   │       └── TriggerConfigValidationResult.cs
│       │   ├── Validators/
│       │   │   └── CreateAutomationRequestValidator.cs
│       │   ├── Middleware/
│       │   │   └── ExceptionHandlingMiddleware.cs
│       │   ├── appsettings.json
│       │   ├── Program.cs
│       │   └── FlowForge.WebApi.csproj
│       │
│       ├── FlowForge.JobAutomator/
│       │   ├── Cache/
│       │   │   ├── AutomationCache.cs
│       │   │   └── AutomationSnapshot.cs            # TriggerSnapshot.TypeId is string
│       │   ├── Clients/
│       │   │   ├── IAutomationApiClient.cs
│       │   │   └── AutomationApiClient.cs
│       │   ├── Evaluators/
│       │   │   ├── ITriggerEvaluator.cs             # TypeId property is string (not enum)
│       │   │   ├── ScheduleTriggerEvaluator.cs
│       │   │   ├── SqlTriggerEvaluator.cs
│       │   │   ├── JobCompletedTriggerEvaluator.cs
│       │   │   ├── WebhookTriggerEvaluator.cs
│       │   │   └── CustomScriptTriggerEvaluator.cs  # Runs Python subprocess
│       │   ├── Conditions/
│       │   │   └── TriggerConditionEvaluator.cs
│       │   ├── Quartz/
│       │   │   ├── ScheduledTriggerJob.cs
│       │   │   └── QuartzScheduleSync.cs
│       │   ├── Workers/
│       │   │   ├── AutomationCacheInitializer.cs
│       │   │   ├── AutomationCacheSyncWorker.cs
│       │   │   ├── AutomationWorker.cs
│       │   │   └── JobCompletedFlagWorker.cs
│       │   ├── appsettings.json                     # CustomScript section (ScriptTempDir, VenvCacheDir)
│       │   ├── Program.cs
│       │   └── FlowForge.JobAutomator.csproj
│       │
│       ├── FlowForge.JobOrchestrator/
│       │   ├── LoadBalancing/
│       │   │   ├── ILoadBalancer.cs
│       │   │   └── RoundRobinLoadBalancer.cs
│       │   ├── Workers/
│       │   │   ├── JobDispatcherWorker.cs
│       │   │   └── HeartbeatMonitorWorker.cs
│       │   ├── appsettings.json
│       │   ├── Program.cs
│       │   └── FlowForge.JobOrchestrator.csproj
│       │
│       ├── FlowForge.WorkflowHost/
│       │   ├── ProcessManagement/
│       │   │   ├── IProcessManager.cs
│       │   │   ├── DockerProcessManager.cs
│       │   │   └── NativeProcessManager.cs
│       │   ├── Workers/
│       │   │   ├── JobConsumerWorker.cs
│       │   │   └── CancelConsumerWorker.cs
│       │   ├── appsettings.json
│       │   ├── Program.cs
│       │   └── FlowForge.WorkflowHost.csproj
│       │
│       └── FlowForge.WorkflowEngine/
│           ├── Handlers/
│           │   ├── IWorkflowHandler.cs
│           │   ├── WorkflowHandlerRegistry.cs
│           │   ├── WorkflowContext.cs
│           │   ├── WorkflowResult.cs
│           │   └── Built-in/
│           │       ├── SendEmailHandler.cs
│           │       ├── HttpRequestHandler.cs
│           │       └── RunScriptHandler.cs
│           ├── Reporting/
│           │   ├── IJobReporter.cs
│           │   └── JobProgressReporter.cs
│           ├── appsettings.json
│           ├── Program.cs
│           └── FlowForge.WorkflowEngine.csproj
│
├── tests/
│   ├── FlowForge.Domain.Tests/
│   │   ├── AutomationTests.cs
│   │   └── TriggerConditionEvaluatorTests.cs
│   ├── FlowForge.JobAutomator.Tests/
│   │   ├── ScheduleTriggerEvaluatorTests.cs
│   │   ├── SqlTriggerEvaluatorTests.cs
│   │   └── CustomScriptTriggerEvaluatorTests.cs
│   ├── FlowForge.JobOrchestrator.Tests/
│   │   └── RoundRobinLoadBalancerTests.cs
│   └── FlowForge.WebApi.Tests/
│       ├── AutomationsControllerTests.cs
│       └── TriggersControllerTests.cs
│
└── deploy/
    ├── docker/
    │   ├── docker-compose.yml
    │   ├── docker-compose.override.yml
    │   └── Dockerfiles/
    │       ├── Dockerfile.WebApi
    │       ├── Dockerfile.JobAutomator     ← must include Python 3 + pip
    │       ├── Dockerfile.JobOrchestrator
    │       ├── Dockerfile.WorkflowHost
    │       └── Dockerfile.WorkflowEngine
    └── k8s/
        ├── namespace.yaml
        ├── configmaps/
        │   └── app-config.yaml
        ├── webapi/
        ├── job-automator/
        ├── job-orchestrator/
        ├── workflow-host/
        └── infrastructure/
```

---

## Domain Entity: Automation

```csharp
public class Automation : BaseEntity<Guid>
{
    public string                 Name          { get; private set; }
    public string?                Description   { get; private set; }
    public Guid                   HostGroupId   { get; private set; }
    public string                 TaskId        { get; private set; }
    public bool                   IsEnabled     { get; private set; }  // default true
    public TriggerConditionNode   ConditionRoot { get; private set; }  // never null
    public IReadOnlyList<Trigger> Triggers      { get; private set; }  // at least 1

    public static Automation Create(...)
    {
        // Throws InvalidAutomationException if triggers empty or conditionRoot null
        // Throws InvalidTriggerConditionException if condition references unknown TriggerName
    }

    public void Enable()  => IsEnabled = true;
    public void Disable() => IsEnabled = false;
}
```

## Domain Entity: Trigger

```csharp
public class Trigger : BaseEntity<Guid>
{
    public Guid   AutomationId { get; private set; }
    public string Name         { get; private set; }   // unique within Automation
    public string TypeId       { get; private set; }   // matches TriggerTypes constants
    public string ConfigJson   { get; private set; }
}
```

`TypeId` is stored as `varchar(100)`. Valid values are the constants in `TriggerTypes`; validation is done in the service layer via `ITriggerTypeRegistry.IsKnown(typeId)` before the entity is created.

## Static Class: TriggerTypes

```csharp
// FlowForge.Domain/Triggers/TriggerTypes.cs
public static class TriggerTypes
{
    public const string Schedule     = "schedule";
    public const string Sql          = "sql";
    public const string JobCompleted = "job-completed";
    public const string Webhook      = "webhook";
    public const string CustomScript = "custom-script";
}
```

**There is no `TriggerType` enum.** All code uses these string constants.

## Value Object: TriggerConditionNode

```csharp
public record TriggerConditionNode(
    ConditionOperator?                   Operator,
    string?                              TriggerName,  // non-null on leaf nodes
    IReadOnlyList<TriggerConditionNode>? Nodes
);
```

## Domain Exceptions

| Exception | Thrown When |
|---|---|
| `InvalidAutomationException` | Empty triggers, null condition, or unknown `TypeId` in service layer |
| `InvalidTriggerConditionException` | Condition references a `TriggerName` not in triggers list |
| `JobNotFoundException` | Job lookup returns null |
| `AutomationNotFoundException` | Automation lookup returns null |
| `InvalidJobTransitionException` | Illegal job status transition |
| `UnknownConnectionIdException` | `ConnectionId` not in config |

---

## .csproj Target Frameworks

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
</PropertyGroup>
```

---

## Dependency Graph

```
FlowForge.Domain          → (no external deps)
FlowForge.Contracts       → (no external deps)
FlowForge.Infrastructure  → Domain, Contracts, EF Core, StackExchange.Redis
FlowForge.WebApi          → Domain, Contracts, Infrastructure
FlowForge.JobAutomator    → Domain, Contracts, Infrastructure, Quartz
FlowForge.JobOrchestrator → Domain, Contracts, Infrastructure
FlowForge.WorkflowHost    → Domain, Contracts, Infrastructure
FlowForge.WorkflowEngine  → Domain, Contracts, Infrastructure
```

---

## Key NuGet Packages

| Package | Used By |
|---|---|
| `Microsoft.EntityFrameworkCore.Design` | Infrastructure |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | Infrastructure |
| `StackExchange.Redis` | Infrastructure |
| `Quartz` | JobAutomator |
| `Microsoft.AspNetCore.SignalR` | WebApi |
| `FluentValidation.AspNetCore` | WebApi |

---

## Event Flow (Redis Streams)

```
[Trigger fires]
  JobAutomator ──[AutomationTriggeredEvent]──► WebApi
  WebApi ──[JobCreatedEvent + ConnectionId]───► JobOrchestrator
  JobOrchestrator ──[JobAssignedEvent]────────► WorkflowHost (per-host stream)
  WorkflowEngine ──[JobStatusChangedEvent]────► WebApi
  WorkflowEngine ──[heartbeat:{jobId} TTL]────► Redis

[Cancel]
  WebApi ──[JobCancelRequestedEvent]──────────► WorkflowHost

[Automation enabled/disabled]
  WebApi ──[AutomationChangedEvent (Updated)]─► JobAutomator → cache + Quartz sync
```

---

## Multi-Database Architecture

```json
{
  "Platform": { "ConnectionString": "..." },
  "JobConnections": {
    "wf-jobs-minion": { "ConnectionString": "...", "Provider": "PostgreSQL" }
  }
}
```

---

## K8s Notes

| Service | Kind | Reason |
|---|---|---|
| WebApi | Deployment | Stateless, scalable |
| JobAutomator | Deployment | Redis consumer groups; **requires Python 3 in image** |
| JobOrchestrator | Deployment (1 replica) | Stateful round-robin |
| WorkflowHost | DaemonSet | One per node |
| WorkflowEngine | (not deployed) | Spawned as child process |