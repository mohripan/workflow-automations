# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**FlowForge** is a distributed workflow orchestration system built on .NET 10. It consists of five cooperating services communicating via a provider-agnostic messaging layer (Redis Streams or Dapr+Kafka).

## Common Commands

### Build
```bash
dotnet build FlowForge.sln
```

### Run Tests
```bash
# Unit tests only (no infrastructure needed)
dotnet test tests/FlowForge.Domain.Tests

# Integration tests (requires Docker — uses Testcontainers)
dotnet test tests/FlowForge.Integration.Tests

# Security tests
dotnet test tests/FlowForge.Security.Tests

# All tests
dotnet test

# Single test class
dotnet test tests/FlowForge.Domain.Tests/FlowForge.Domain.Tests.csproj --filter "ClassName=JobStateMachineTests"

# Single test method
dotnet test tests/FlowForge.Domain.Tests/FlowForge.Domain.Tests.csproj --filter "ClassName=TriggerConditionEvaluatorTests&MethodName=ShouldEvaluateAndConditionCorrectly"

# Watch mode
dotnet watch test --project tests/FlowForge.Domain.Tests
```

### Start Local Infrastructure
```bash
# Redis mode (default)
cd deploy/docker
docker compose up -d

# Dapr + Kafka mode
cd deploy/docker
docker compose -f compose.yaml -f compose.dapr.yaml up -d --build
```

### Run Services Locally
```bash
dotnet run --project src/services/FlowForge.WebApi         # http://localhost:5015
dotnet run --project src/services/FlowForge.JobAutomator
dotnet run --project src/services/FlowForge.JobOrchestrator
dotnet run --project src/services/FlowForge.WorkflowHost
```

### EF Core Migrations
```bash
dotnet ef database update \
  --project src/shared/FlowForge.Infrastructure \
  --startup-project src/services/FlowForge.WebApi \
  --context PlatformDbContext
```

## Architecture

### Service Topology

```
REST/SignalR clients
       ↓
   WebApi (8080)              ← CRUD, job lifecycle, real-time SignalR hub
       ↕ (Redis Streams + Outbox)
   JobAutomator (8081)        ← evaluates triggers every 30s, publishes AutomationTriggeredEvent
   JobOrchestrator (8082)     ← routes jobs to hosts, monitors heartbeats
       ↓ (host-specific streams)
   WorkflowHost (8083)        ← spawns child processes, maintains heartbeat
       ↓ (child process per job)
   WorkflowEngine (console)   ← executes single job, publishes status back
```

### Layer Dependency Rules (DDD)
- `FlowForge.Domain` → no dependencies
- `FlowForge.Contracts` → no dependencies
- `FlowForge.Infrastructure` → Domain + Contracts only
- Service projects (WebApi, JobAutomator, etc.) → shared projects only, **never each other**

### Communication: Provider-Agnostic Messaging
The messaging layer supports two providers, selected via `Messaging:Provider` config:

**Redis mode** (default, `"redis"`):
1. Write event to `OutboxMessage` table in the same transaction as the domain entity change
2. `OutboxRelayWorker` polls and publishes via `IMessagePublisher` → Redis Streams
3. `BackgroundService` workers pull from streams via `IMessageConsumer`

**Dapr mode** (`"dapr"`):
1. Same outbox pattern — `OutboxRelayWorker` publishes via `IMessagePublisher` → Dapr pub/sub → Kafka
2. Dapr sidecars push events to HTTP subscription endpoints (`/dapr/{topicName}`)
3. `IEventHandler<TEvent>` classes contain all business logic, shared by both modes

Key topics: `automation-triggered`, `automation-changed`, `job-created`, `host-{hostName}`, `job-status-changed`, `job-cancel-requested`, `dlq`

Abstractions: `IMessagePublisher`, `IEventHandler<TEvent>`, `IMessagingInfrastructure`, `IDlqReader`, `IDlqWriter`

Docker: `compose.yaml` (Redis mode) or `compose.yaml` + `compose.dapr.yaml` (Dapr+Kafka mode)

### Trigger System
Triggers use a string `TypeId` and a `ConfigJson` blob. Each type implements `ITriggerTypeDescriptor` (declares schema + sensitive fields) and an `ITriggerEvaluator`. Built-in types: `schedule`, `sql`, `webhook`, `job-completed`, `custom-script`.

Sensitive trigger fields are encrypted at rest with AES-256-GCM (`enc:v1:<base64>`). API responses always redact them to `"***"`.

### Multi-Database Setup
- **flowforge_platform** (port 5432) — Automations, Triggers, HostGroups, Outbox
- **flowforge_minion / flowforge_titan** (ports 5433/5434) — Jobs, isolated by HostGroup (`ConnectionId`)
- **flowforge_quartz** (port 5435) — Quartz.NET clustered scheduler state
- Job repositories are resolved via `GetRequiredKeyedService<IJobRepository>(connectionId)`

### WorkflowEngine Process Contract
The engine is spawned as a child process with environment variables `JOB_ID`, `JOB_AUTOMATION_ID`, `CONNECTION_ID`. It resolves the handler by `job.TaskId` from `ITaskTypeRegistry`, executes it, and publishes `JobStatusChangedEvent`.

### Authentication
- Keycloak realm `flowforge` issues JWT bearer tokens
- Roles: `admin`, `operator`, `viewer`; policy `InternalService` uses M2M `azp` claim
- Exceptions to auth: `/health/*` and webhook endpoints
- Rate limiting: webhooks 30 req/min per IP; global 300 req/min per user

## Key Documentation

Detailed specs live in `docs/`:
- `AGENTS.md` — architecture overview for AI assistants
- `CONVENTIONS.md` — mandatory DDD/coding/git conventions
- `SPECS.md` — full NuGet dependency list and solution layout
- `TRIGGERS.md` — trigger type system and how to add new types
- `WEBAPI.md`, `JOBAUTOMATOR.md`, `JOBORCHESTRATOR.md`, `WORKFLOWHOST.md`, `WORKFLOWENGINE.md` — per-service details
- `ROADMAP.md` — completed security hardening plan

## Test Conventions
- `FlowForge.Domain.Tests` — pure unit tests, no infrastructure, use NSubstitute for mocks
- `FlowForge.Integration.Tests` — uses Testcontainers (real PostgreSQL + Redis spun up per run)
- `FlowForge.Security.Tests` — auth, encryption, rate limiting scenarios
- All projects: xUnit + FluentAssertions, `TreatWarningsAsErrors=true`, nullable enabled
