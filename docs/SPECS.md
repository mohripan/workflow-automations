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
│   │   │   │   ├── Automation.cs                    # Automation definition
│   │   │   │   ├── Job.cs                           # Job instance
│   │   │   │   ├── Trigger.cs                       # Trigger configuration
│   │   │   │   ├── WorkflowHost.cs                  # Host registration record
│   │   │   │   └── HostGroup.cs                     # Group of hosts
│   │   │   ├── Enums/
│   │   │   │   ├── JobStatus.cs
│   │   │   │   ├── TriggerType.cs                   # Schedule | Sql | JobCompleted | Webhook
│   │   │   │   └── ConditionOperator.cs             # And | Or
│   │   │   ├── ValueObjects/
│   │   │   │   └── TriggerCondition.cs              # Condition tree node
│   │   │   ├── Exceptions/
│   │   │   │   ├── DomainException.cs
│   │   │   │   ├── JobNotFoundException.cs
│   │   │   │   └── AutomationNotFoundException.cs
│   │   │   └── FlowForge.Domain.csproj              # No external NuGet deps
│   │   │
│   │   ├── FlowForge.Contracts/                     # Redis Stream message schemas
│   │   │   ├── Events/
│   │   │   │   ├── AutomationTriggeredEvent.cs      # JobAutomator → WebApi
│   │   │   │   ├── JobCreatedEvent.cs               # WebApi → JobOrchestrator
│   │   │   │   ├── JobAssignedEvent.cs              # JobOrchestrator → WorkflowHost
│   │   │   │   ├── JobStatusChangedEvent.cs         # WorkflowEngine → WebApi
│   │   │   │   └── JobCancelRequestedEvent.cs       # WebApi → WorkflowHost
│   │   │   └── FlowForge.Contracts.csproj           # DTO only — minimal deps
│   │   │
│   │   └── FlowForge.Infrastructure/                # Shared infra implementations
│   │       ├── Persistence/
│   │       │   ├── FlowForgeDbContext.cs
│   │       │   ├── Migrations/
│   │       │   └── Configurations/                  # EF Core Fluent API
│   │       │       ├── AutomationConfiguration.cs
│   │       │       ├── JobConfiguration.cs
│   │       │       ├── TriggerConfiguration.cs
│   │       │       ├── WorkflowHostConfiguration.cs
│   │       │       └── HostGroupConfiguration.cs
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
│   │       │   ├── IJobRepository.cs
│   │       │   ├── IAutomationRepository.cs
│   │       │   └── IHostGroupRepository.cs
│   │       ├── ServiceCollectionExtensions.cs       # AddInfrastructure(IConfiguration)
│   │       └── FlowForge.Infrastructure.csproj
│   │
│   └── services/
│       │
│       ├── FlowForge.WebApi/
│       │   ├── Controllers/
│       │   │   ├── AutomationsController.cs
│       │   │   ├── JobsController.cs
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
│           ├── Activities/
│           │   ├── IActivity.cs
│           │   ├── ActivityContext.cs               # Input/output bag per activity
│           │   ├── SendEmailActivity.cs
│           │   ├── RunScriptActivity.cs             # Python/shell runner
│           │   └── HttpRequestActivity.cs
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
                                                │ creates Job (DB, status=Pending)
                                                │
  WebApi ──[JobCreatedEvent]──────────────────► JobOrchestrator
                                                │ picks host via round-robin
                                                │ updates Job (status=Started, hostId)
                                                │
  JobOrchestrator ──[JobAssignedEvent]────────► WorkflowHost (per-host stream)
                                                │ spawns WorkflowEngine child process
                                                │
  WorkflowEngine ──[JobStatusChangedEvent]────► WebApi (InProgress / Completed / Error)
  WorkflowEngine ──[heartbeat:{jobId} TTL]────► Redis (every 5s, TTL 30s)

[Cancel request]
  WebApi ──[JobCancelRequestedEvent]──────────► WorkflowHost
                                                │ kills child process (grace period)
                                                │
  WorkflowEngine ──[JobStatusChangedEvent]────► WebApi (status=Cancelled)
```

---

## K8s Notes

| Service | Kind | Reason |
|---|---|---|
| WebApi | Deployment | Stateless, horizontally scalable |
| JobAutomator | Deployment | Scale via Redis consumer groups |
| JobOrchestrator | Deployment (1 replica) | Stateful round-robin; add leader election for HA |
| WorkflowHost | DaemonSet | One per node; `hostId` derived from node name |
| WorkflowEngine | (not deployed) | Spawned by WorkflowHost as child process |