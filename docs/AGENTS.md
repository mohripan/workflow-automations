# AGENTS.md — FlowForge Workflow Orchestrator

## What Is This Project?

FlowForge is a **workflow orchestration system** built from scratch in .NET 8. The goal is to learn and improve upon a legacy system by applying modern architecture patterns: event-driven communication, clean domain separation, and cloud-native deployment via Docker/Kubernetes.

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
- One or more **Triggers** (schedule, SQL, job-completed, webhook)
- **Trigger Conditions** — logical expression using AND/OR and parentheses
- A **Host Group** — determines which pool of hosts will run the resulting job
- A **TaskId** — identifies which workflow handler the resulting job will execute

### Job
A unit of work created when an Automation's trigger conditions are met.
- Always belongs to one Automation
- Carries a `TaskId` (inherited from the Automation) that determines what the job executes
- Has a `HostGroupId` and (after assignment) a `HostId`
- Stored in the **database belonging to its Host Group** (see Host Group below)
- Progresses through a well-defined status lifecycle

### Job Status Lifecycle
```
Pending → Started → InProgress → Completed
                              └→ Error
                              └→ CompletedUnsuccessfully
       └→ Removed
       (from any active state, via cancel request)
       → Cancel → Cancelled
```

### Host Group
A named group of Workflow Host instances. Each host group has:
- Its own **dedicated database** (connection string identified by `ConnectionId`, e.g. `wf-jobs-minion`)
- A pool of hosts load-balanced via round-robin for job dispatch
- Jobs are stored in and queried from the host group's own database

This allows host groups to use different database providers, be on separate servers, and scale independently. For example:
- `minion` group → lightweight jobs → PostgreSQL on a small server
- `titan` group → long-running jobs → PostgreSQL on a dedicated server

Automations are stored in a single **platform database**, separate from all host group databases, because they are definitions rather than execution data.

### TaskId
A string identifier on both Automation and Job that determines which **Workflow Handler** executes the job. The Workflow Engine resolves this at runtime via a `WorkflowHandlerRegistry`.

Built-in handler types:
- `send-email` — sends an email via SMTP/SendGrid
- `http-request` — makes an outbound HTTP call
- `run-script` — executes a Python, JavaScript, or shell script (sandboxed)

Custom handlers can be added by implementing `IWorkflowHandler` and registering it, or by using `run-script` with a custom script file — no DLL compilation required.

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
| Heartbeat | HTTP call to Web API every 5s | Redis key with TTL (no API hop) |
| Process kill | Windows Job Object only | Abstracted `IProcessManager` (Docker-native + Linux fallback) |
| Custom triggers | Compile DLL, drop in folder | Webhook trigger + `run-script` handler |
| Custom workflows | Compile DLL, reflection on namespace | `IWorkflowHandler` registry + `run-script` |
| Job storage | Shared database for all jobs | Per-host-group database via `ConnectionId` |
| DB provider flexibility | Single shared provider | Keyed Services — per-group provider config |
| Host discovery | SignalR hub connection tracking | Redis-registered host records |
| Deployment | Windows Server on-prem | Docker + Kubernetes |

---

## Repository Layout

See **SPECS.md** for the full solution/project structure.
See **CONVENTIONS.md** for coding standards, DDD rules, and shared patterns.

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
- `make` (optional, for convenience commands)

### Run Everything Locally
```bash
cd deploy/docker
docker compose up -d          # starts Redis + PostgreSQL
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
- Frontend UI (API-first, can be added later)
- Auth Server / OAuth2 (placeholder structure exists, not implemented)
- Multi-tenancy
- Custom plugin triggers beyond the built-in types