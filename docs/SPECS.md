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
├── JOBAUTOMATOR.md
├── JOBORCHESTRATOR.md
├── WORKFLOWHOST.md
├── WORKFLOWENGINE.md
│
├── src/
│   │
│   ├── shared/                                      # Referenced by ALL services
│   │   │
│   │   ├── FlowForge.Domain/                        # Pure domain — zero external deps
│   │   │   ├── Entities/
│   │   │   │   ├── Automation.cs                    # Automation definition (has TaskId)
│   │   │   │   ├── Job.cs                           # Job instance (has TaskId + ConnectionId)
│   │   │   │   ├── Trigger.cs                       # Trigger configuration
│   │   │   │   ├── WorkflowHost.cs                  # Host registration record
│   │   │   │   └── HostGroup.cs                     # Group of hosts (has ConnectionId)
│   │   │   ├── Enums/
│   │   │   │   ├── JobStatus.cs
│   │   │   │   ├── TriggerType.cs                   # Schedule | Sql | JobCompleted | Webhook
│   │   │   │   └── ConditionOperator.cs             # And | Or
│   │   │   ├── ValueObjects/
│   │   │   │   └── TriggerCondition.cs              # Condition tree node
│   │   │   ├── Exceptions/
│   │   │   │   ├── DomainException.cs
│   │   │   │   ├── JobNotFoundException.cs
│   │   │   │   ├── AutomationNotFoundException.cs
│   │   │   │   └── UnknownConnectionIdException.cs
│   │   │   └── FlowForge.Domain.csproj              # No external NuGet deps
│   │   │
│   │   ├── FlowForge.Contracts/                     # Redis Stream message schemas
│   │   │   ├── Events/
│   │   │   │   ├── AutomationTriggeredEvent.cs      # JobAutomator → WebApi
│   │   │   │   ├── JobCreatedEvent.cs               # WebApi → JobOrchestrator (has ConnectionId)
│   │   │   │   ├── JobAssignedEvent.cs              # JobOrchestrator → WorkflowHost
│   │   │   │   ├── JobStatusChangedEvent.cs         # WorkflowEngine → WebApi (has ConnectionId)
│   │   │   │   └── JobCancelRequestedEvent.cs       # WebApi → WorkflowHost
│   │   │   └── FlowForge.Contracts.csproj           # DTO only — minimal deps
│   │   │
│   │   └── FlowForge.Infrastructure/                # Shared infra implementations
│   │       ├── Persistence/
│   │       │   ├── Platform/                        # Platform DB: Automations, HostGroups
│   │       │   │   ├── PlatformDbContext.cs
│   │       │   │   ├── Migrations/
│   │       │   │   └── Configurations/
│   │       │   │       ├── AutomationConfiguration.cs
│   │       │   │       ├── TriggerConfiguration.cs
│   │       │   │       ├── WorkflowHostConfiguration.cs
│   │       │   │       └── HostGroupConfiguration.cs
│   │       │   └── Jobs/                            # Per-host-group DB: Jobs only
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
│   │       │       ├── RedisStreamConsumer.cs       # Consumer group support
│   │       │       └── StreamNames.cs               # Stream name constants
│   │       ├── Caching/
│   │       │   ├── IRedisService.cs
│   │       │   └── RedisService.cs                  # Heartbeat TTL, scalars, cache
│   │       ├── Repositories/
│   │       │   ├── IJobRepository.cs                # Resolved by ConnectionId at runtime
│   │       │   ├── IAutomationRepository.cs
│   │       │   └── IHostGroupRepository.cs
│   │       ├── MultiDb/
│   │       │   ├── JobsDbContextFactory.cs          # Creates JobsDbContext per ConnectionId
│   │       │   └── ConnectionRegistry.cs            # Maps ConnectionId → connection string + provider
│   │       ├── ServiceCollectionExtensions.cs       # AddInfrastructure(IConfiguration)
│   │       └── FlowForge.Infrastructure.csproj
│   │
│   └── services/
│       │
│       ├── FlowForge.WebApi/
│       │   ├── Controllers/
│       │   │   ├── AutomationsController.cs
│       │   │   ├── JobsController.cs                # Route: /api/{connectionId}/jobs
│       │   │   ├── TriggersController.cs
│       │   │   └── HostGroupsController.cs
│       │   ├── Hubs/
│       │   │   └── JobStatusHub.cs                  # SignalR — frontend only
│       │   ├── DTOs/
│       │   │   ├── Requests/
│       │   │   │   ├── CreateAutomationRequest.cs
│       │   │   │   ├── UpdateAutomationRequest.cs
│       │   │   │   └── CancelJobRequest.cs
│       │   │   └── Responses/
│       │   │       ├── AutomationResponse.cs
│       │   │       └── JobResponse.cs
│       │   ├── Middleware/
│       │   │   └── ExceptionHandlingMiddleware.cs
│       │   ├── appsettings.json
│       │   ├── appsettings.Development.json
│       │   ├── Program.cs
│       │   └── FlowForge.WebApi.csproj
│       │
│       ├── FlowForge.JobAutomator/
│       │   ├── Evaluators/
│       │   │   ├── ITriggerEvaluator.cs
│       │   │   ├── ScheduleTriggerEvaluator.cs      # Quartz.NET-based
│       │   │   ├── SqlTriggerEvaluator.cs           # Direct DB polling
│       │   │   ├── JobCompletedTriggerEvaluator.cs  # Consumes JobStatusChangedEvent
│       │   │   └── WebhookTriggerEvaluator.cs       # Receives via HTTP (passthrough)
│       │   ├── Conditions/
│       │   │   └── TriggerConditionEvaluator.cs     # AND/OR tree evaluator
│       │   ├── Workers/
│       │   │   └── AutomationWorker.cs
│       │   ├── appsettings.json
│       │   ├── Program.cs
│       │   └── FlowForge.JobAutomator.csproj
│       │
│       ├── FlowForge.JobOrchestrator/
│       │   ├── LoadBalancing/
│       │   │   ├── ILoadBalancer.cs
│       │   │   └── RoundRobinLoadBalancer.cs
│       │   ├── Workers/
│       │   │   ├── JobDispatcherWorker.cs           # Consume JobCreatedEvent
│       │   │   └── HeartbeatMonitorWorker.cs        # Monitor Redis TTL
│       │   ├── appsettings.json
│       │   ├── Program.cs
│       │   └── FlowForge.JobOrchestrator.csproj
│       │
│       ├── FlowForge.WorkflowHost/
│       │   ├── ProcessManagement/
│       │   │   ├── IProcessManager.cs
│       │   │   ├── DockerProcessManager.cs
│       │   │   └── NativeProcessManager.cs          # Linux process group fallback
│       │   ├── Workers/
│       │   │   ├── JobConsumerWorker.cs             # Consumes per-host stream
│       │   │   └── CancelConsumerWorker.cs          # Consumes cancel requests
│       │   ├── appsettings.json
│       │   ├── Program.cs
│       │   └── FlowForge.WorkflowHost.csproj
│       │
│       └── FlowForge.WorkflowEngine/
│           ├── Handlers/
│           │   ├── IWorkflowHandler.cs              # Interface all handlers implement
│           │   ├── WorkflowHandlerRegistry.cs       # Resolves TaskId → IWorkflowHandler
│           │   ├── WorkflowContext.cs               # Input params + output store
│           │   ├── WorkflowResult.cs                # Success | Failed | Cancelled
│           │   ├── Built-in/
│           │   │   ├── SendEmailHandler.cs          # TaskId: "send-email"
│           │   │   ├── HttpRequestHandler.cs        # TaskId: "http-request"
│           │   │   └── RunScriptHandler.cs          # TaskId: "run-script" (Python/JS/shell)
│           ├── Reporting/
│           │   ├── IJobReporter.cs
│           │   └── JobProgressReporter.cs           # Publish status + heartbeat
│           ├── appsettings.json
│           ├── Program.cs
│           └── FlowForge.WorkflowEngine.csproj
│
├── tests/
│   ├── FlowForge.Domain.Tests/
│   │   └── TriggerConditionEvaluatorTests.cs
│   ├── FlowForge.JobAutomator.Tests/
│   │   ├── ScheduleTriggerEvaluatorTests.cs
│   │   └── SqlTriggerEvaluatorTests.cs
│   ├── FlowForge.JobOrchestrator.Tests/
│   │   └── RoundRobinLoadBalancerTests.cs
│   └── FlowForge.WebApi.Tests/
│       └── AutomationsControllerTests.cs
│
└── deploy/
    ├── docker/
    │   ├── docker-compose.yml                       # Full local dev stack
    │   ├── docker-compose.override.yml              # Hot reload + exposed ports
    │   └── Dockerfiles/
    │       ├── Dockerfile.WebApi
    │       ├── Dockerfile.JobAutomator
    │       ├── Dockerfile.JobOrchestrator
    │       ├── Dockerfile.WorkflowHost
    │       └── Dockerfile.WorkflowEngine
    └── k8s/
        ├── namespace.yaml
        ├── configmaps/
        │   └── app-config.yaml
        ├── webapi/
        │   ├── deployment.yaml
        │   └── service.yaml
        ├── job-automator/
        │   └── deployment.yaml                      # Scalable via consumer groups
        ├── job-orchestrator/
        │   └── deployment.yaml                      # Single replica
        ├── workflow-host/
        │   └── daemonset.yaml                       # 1 per node
        └── infrastructure/
            ├── redis.yaml
            └── postgres.yaml
```

---

## .csproj Target Frameworks

All projects target `net10.0`.

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
# Shared (innermost first)
FlowForge.Domain          → (no external deps)
FlowForge.Contracts       → (no external deps — pure DTOs)
FlowForge.Infrastructure  → Domain, Contracts, EF Core, StackExchange.Redis

# Services (all depend on shared)
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

---

## Event Flow (Redis Streams)

```
[Trigger fires]
  JobAutomator ──[AutomationTriggeredEvent]──► WebApi
                                                │ creates Job in host group's DB
                                                │ (DB selected via ConnectionId)
                                                │
  WebApi ──[JobCreatedEvent + ConnectionId]───► JobOrchestrator
                                                │ picks host via round-robin
                                                │ updates Job (status=Started, hostId)
                                                │
  JobOrchestrator ──[JobAssignedEvent]────────► WorkflowHost (per-host stream)
                                                │ spawns WorkflowEngine child process
                                                │ passes JOB_ID + CONNECTION_ID env vars
                                                │
  WorkflowEngine ──[JobStatusChangedEvent     ► WebApi (InProgress / Completed / Error)
                     + ConnectionId]            │ WebApi updates correct host group DB
  WorkflowEngine ──[heartbeat:{jobId} TTL]────► Redis (every 5s, TTL 30s)

[Cancel request]
  WebApi ──[JobCancelRequestedEvent]──────────► WorkflowHost
                                                │ kills child process (grace period)
                                                │
  WorkflowEngine ──[JobStatusChangedEvent]────► WebApi (status=Cancelled)
```

---

## Multi-Database Architecture

Each `HostGroup` has a `ConnectionId` (e.g. `wf-jobs-minion`, `wf-jobs-titan`) that maps to a dedicated database. Jobs are stored in and queried from their host group's database. Automations, HostGroups, and WorkflowHosts are stored in a single **platform database**.

```json
// appsettings.json (WebApi, JobOrchestrator, WorkflowEngine)
{
  "Platform": {
    "ConnectionString": "Host=postgres;Database=flowforge_platform;..."
  },
  "JobConnections": {
    "wf-jobs-minion": {
      "ConnectionString": "Host=postgres;Database=flowforge_minion;...",
      "Provider": "PostgreSQL"
    },
    "wf-jobs-titan": {
      "ConnectionString": "Host=postgres-titan;Database=flowforge_titan;...",
      "Provider": "PostgreSQL"
    }
  }
}
```

`ConnectionRegistry` maps a `ConnectionId` string to the correct `JobsDbContext` at runtime. Controllers and consumers resolve `IJobRepository` by `ConnectionId` using .NET Keyed Services — no Service Locator pattern required.

---

## K8s Notes

| Service | Kind | Reason |
|---|---|---|
| WebApi | Deployment | Stateless, horizontally scalable |
| JobAutomator | Deployment | Scale via Redis consumer groups |
| JobOrchestrator | Deployment (1 replica) | Stateful round-robin; add leader election for HA |
| WorkflowHost | DaemonSet | One per node; `hostId` derived from node name |
| WorkflowEngine | (not deployed) | Spawned by WorkflowHost as child process |