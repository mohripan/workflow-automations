# AGENTS.md — FlowForge Workflow Orchestrator

## What Is This Project?

FlowForge is a **workflow orchestration system** built from scratch in .NET 10. The goal is to learn and improve upon a legacy system by applying modern architecture patterns: event-driven communication, clean domain separation, and cloud-native deployment via Docker/Kubernetes.

The system allows users to define **Automations** — combinations of triggers and conditions — that automatically create and dispatch **Jobs** to distributed worker hosts for execution.

---

## High-Level Architecture

```
┌─────────────┐     ┌──────────────┐     ┌───────────────────┐
│   Frontend  │────▶│   Web API    │────▶│  Redis Streams    │
└─────────────┘     └──────────────┘     └──────┬────────────┘
                                                 │
              ┌──────────────┬──────────────┬────┘
              ▼              ▼              ▼
       ┌────────────┐ ┌────────────┐ ┌────────────┐
       │  Job       │ │  Job       │ │  Workflow  │
       │ Automator  │ │Orchestrator│ │   Host     │
       └────────────┘ └────────────┘ └─────┬──────┘
                                           │ spawns
                                      ┌────▼──────┐
                                      │ Workflow  │
                                      │  Engine   │
                                      └───────────┘
```

---

## Services Overview

| Service | Type | Responsibility |
|---|---|---|
| **Web API** | ASP.NET Core | REST endpoints, SignalR for frontend real-time updates |
| **Job Automator** | Worker Service | Evaluate automation triggers, publish job creation events |
| **Job Orchestrator** | Worker Service | Consume job events, assign jobs to hosts via round-robin |
| **Workflow Host** | Worker Service | Receive assigned jobs, manage engine child processes |
| **Workflow Engine** | Console App (child process) | Execute a single job, report progress via Redis |

---

## Core Concepts

### Automation

User-defined entity that ties together:
- One or more **Triggers** — **at least one required**
- **Trigger Conditions** — logical AND/OR expression referencing triggers by `Name` — **required; must not be null**
- A **Host Group** — determines which pool of hosts runs the resulting job
- A **TaskId** — identifies which workflow handler executes the job
- An **IsEnabled** flag — when `false`, no jobs are created regardless of trigger state

**Domain invariants enforced on `Automation`:**
1. `Triggers` must contain at least one entry.
2. `ConditionRoot` must not be null.
3. Every `TriggerName` referenced in the condition tree must match the `Name` of an existing trigger on the same automation.
4. A disabled automation never causes a job to be created.

### Trigger

A Trigger belongs to one Automation. Key fields:

| Field | Type | Description |
|---|---|---|
| `Id` | `Guid` | Database PK — used for internal Redis key generation |
| `Name` | `string` | User-assigned label, **unique within the Automation** — used in condition expressions |
| `TypeId` | `string` | Matches a constant in `TriggerTypes` (e.g. `"schedule"`, `"custom-script"`) |
| `ConfigJson` | `string` | Type-specific JSON config |

**There is no `TriggerType` enum.** `TypeId` is a plain string. Built-in type IDs are string constants in `TriggerTypes`:

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

This design makes the type system open: new types require no enum changes, no code recompilation for the trigger discovery endpoints.

### Trigger Type System

Each `TypeId` has a corresponding **`ITriggerTypeDescriptor`** that self-describes:
- A **config schema** (`TriggerConfigSchema`) — list of `ConfigField` records with name, label, data type, required flag, description, and default value. Used by the frontend to render the correct form.
- A **`ValidateConfig(configJson)`** method — called by the API before saving a trigger to the database.

See **TRIGGERS.md** for full details on all built-in descriptors and the `custom-script` type.

### Custom Script Trigger

Users who need a trigger condition beyond the built-ins (schedule, SQL, job-completed, webhook) can use **`custom-script`**: a built-in trigger type where the user provides a Python 3 script inside the `ConfigJson`. FlowForge runs the script on a polling interval in a sandboxed subprocess. The trigger fires when the script exits with code 0 and prints `"true"` to stdout.

This handles scenarios like:
- Checking an external REST API response
- Reading from a file, S3 bucket, or message queue not covered by SQL
- Combining multiple data sources in custom logic

### Trigger Condition

A recursive AND/OR tree where leaf nodes reference a `TriggerName`. Required; must not be null.

Leaf node example: `{ "triggerName": "daily-schedule" }`

Composite node example:
```json
{
  "operator": "And",
  "nodes": [
    { "triggerName": "daily-schedule" },
    { "triggerName": "etl-ready" }
  ]
}
```

### Job

A unit of work created when an Automation's trigger conditions are met **and the automation is enabled**.

### Job Status Lifecycle

```
Pending → Started → InProgress → Completed
                              └→ Error
                              └→ CompletedUnsuccessfully
       └→ Removed
       → Cancel → Cancelled
```

### Host Group

A named group of Workflow Host instances with its own dedicated database (`ConnectionId`). Jobs are stored in and queried from the host group's own database.

### TaskId

A string identifier on both Automation and Job determining which **Workflow Handler** the engine executes.

---

## Communication Patterns

| From | To | Channel |
|---|---|---|
| Job Automator | Web API | Redis Stream |
| Web API | Job Orchestrator | Redis Stream |
| Job Orchestrator | Workflow Host | Redis Stream (per-host stream) |
| Workflow Engine | Web API | Redis Stream |
| Web API (cancel) | Workflow Host | Redis Stream |
| Web API | Frontend | SignalR |
| Workflow Engine | Redis | Direct write (heartbeat TTL key) |
| Job Orchestrator | Redis | Direct read (heartbeat monitor) |

---

## What Makes This Better Than the Legacy System

| Area | Legacy | FlowForge |
|---|---|---|
| Trigger evaluation | Short-poll Web API every N seconds | Event-driven via Redis Streams |
| Job status updates | Workflow Engine polls Web API | Publish event to stream |
| Heartbeat | HTTP call to Web API every 5s | Redis key with TTL |
| Process kill | Windows Job Object only | Abstracted `IProcessManager` |
| Custom triggers | Compile DLL, drop in folder | `custom-script` trigger type — Python inline |
| Trigger discovery | Hardcoded in frontend | `GET /api/triggers/types` — schema-driven |
| Condition authoring | TriggerId GUIDs in expressions | Human-readable TriggerName strings |
| Automation toggle | No enable/disable concept | `IsEnabled`; dedicated endpoints |
| Job storage | Shared database | Per-host-group database via `ConnectionId` |
| Deployment | Windows Server on-prem | Docker + Kubernetes |

---

## Repository Layout

See **SPECS.md** for the full solution/project structure.
See **CONVENTIONS.md** for coding standards, DDD rules, and shared patterns.
See **TRIGGERS.md** for the trigger type system, config schemas, custom-script evaluator, and `TriggersController`.

Per-service deep dives:
- **WEBAPI.md** — REST endpoints, background consumers, SignalR hub, webhook flow
- **JOBAUTOMATOR.md** — trigger evaluation, condition trees, scheduler
- **JOBORCHESTRATOR.md** — job dispatching, load balancing, heartbeat monitoring
- **WORKFLOWHOST.md** — process management, job consumption, host registration
- **WORKFLOWENGINE.md** — activity execution, progress reporting, cancellation

---

## Development Setup

### Prerequisites
- .NET 10 SDK
- Docker Desktop
- Python 3 (only needed locally if running `custom-script` triggers outside Docker)

### Run Everything Locally

```bash
cd deploy/docker
docker compose up -d
dotnet run --project src/services/FlowForge.WebApi
dotnet run --project src/services/FlowForge.JobAutomator
dotnet run --project src/services/FlowForge.JobOrchestrator
dotnet run --project src/services/FlowForge.WorkflowHost
```

### Run Tests

```bash
dotnet test
```

---

## Out of Scope (for now)
- Frontend UI (API-first)
- Auth Server / OAuth2
- Multi-tenancy
- Reusable named custom trigger definitions (saved scripts shared across automations)