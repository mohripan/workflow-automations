# FlowForge

A workflow orchestration system built in .NET 10. Users define **Automations** — trigger conditions that automatically create and dispatch **Jobs** to distributed worker hosts for execution.

## Architecture

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

| Service | Responsibility |
|---|---|
| **Web API** | REST endpoints, SignalR real-time updates, webhook ingestion |
| **Job Automator** | Evaluates trigger conditions, publishes job creation events |
| **Job Orchestrator** | Consumes job events, assigns jobs to hosts via round-robin |
| **Workflow Host** | Receives assigned jobs, manages engine child processes |
| **Workflow Engine** | Executes a single job, reports progress via Redis |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker Desktop](https://www.docker.com/products/docker-desktop)
- Python 3 (only needed locally for `custom-script` triggers outside Docker)

## Quick Start

**1. Start infrastructure**

```bash
cd deploy/docker
docker compose up -d
```

This starts:
- `flowforge-db-platform` — PostgreSQL (port 5432) — automations, host groups
- `flowforge-db-minion` — PostgreSQL (port 5433) — jobs for the `Minion` host group
- `flowforge-db-titan` — PostgreSQL (port 5434) — jobs for the `Titan` host group
- `flowforge-db-quartz` — PostgreSQL (port 5435) — Quartz.NET clustered job store
- `flowforge-redis` — Redis (port 6379) — event streams and trigger flags
- `flowforge-jaeger` — Jaeger (UI: port 16686, OTLP: port 4317) — distributed traces

**2. Run the services**

```bash
dotnet run --project src/services/FlowForge.WebApi
dotnet run --project src/services/FlowForge.JobAutomator
dotnet run --project src/services/FlowForge.JobOrchestrator
dotnet run --project src/services/FlowForge.WorkflowHost
```

The Web API starts on `http://localhost:5015`. Migrations are applied automatically on startup.

**3. Run tests**

```bash
dotnet test
```

## Key Concepts

### Automation

Combines one or more **Triggers** with a **Condition** (AND/OR tree) and targets a **Host Group**. When enabled and all conditions are met, a **Job** is created.

### Trigger Types

| Type ID | Description |
|---|---|
| `schedule` | Fires on a cron schedule (6-part, UTC) |
| `sql` | Fires when a SQL query returns a non-empty result set |
| `job-completed` | Fires when a specific job finishes |
| `webhook` | Fires on HTTP POST to `/api/automations/{id}/webhook` |
| `custom-script` | Fires when a user-provided Python 3 script exits 0 and prints `true` |

### Job Status Lifecycle

```
Pending → Started → InProgress → Completed
                              └→ Error
                              └→ CompletedUnsuccessfully
       └→ Removed
       → Cancel → Cancelled
```

### Host Groups

A named pool of Workflow Host instances, each with its own dedicated jobs database (`ConnectionId`).

## Observability

Distributed traces are exported via OTLP to Jaeger. Open `http://localhost:16686` to explore traces across all services.

Each service emits spans under `FlowForge.{ServiceName}`. Trace context is propagated through Redis Stream messages via the `traceparent` field.

## Project Structure

```
src/
  shared/
    FlowForge.Domain          # Entities, value objects, domain exceptions
    FlowForge.Contracts       # Event DTOs shared across services
    FlowForge.Infrastructure  # EF Core, Redis messaging, Quartz, telemetry
  services/
    FlowForge.WebApi
    FlowForge.JobAutomator
    FlowForge.JobOrchestrator
    FlowForge.WorkflowHost
    FlowForge.WorkflowEngine
deploy/
  docker/
    compose.yaml
    quartz-postgresql.sql     # Quartz.NET DDL, auto-applied on first container start
docs/
  AGENTS.md        # Full system overview for AI agents
  CONVENTIONS.md   # Coding standards and DDD rules
  SPECS.md         # Solution structure and database schema
  TRIGGERS.md      # Trigger type system and config schemas
  WEBAPI.md        # REST endpoints and background workers
  JOBAUTOMATOR.md  # Trigger evaluation and Quartz scheduling
  JOBORCHESTRATOR.md
  WORKFLOWHOST.md
  WORKFLOWENGINE.md
  ROADMAP.md       # Planned improvements
```
