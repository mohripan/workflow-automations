# ROADMAP.md — FlowForge Next Development

This document tracks the next phase of FlowForge development. The focus is on making the system actually runnable end-to-end: a real automation that creates a job, the orchestrator picks it up, a host executes it, and observable results come back. Items are ordered by dependency — each item builds on the ones before it.

---

## Status Legend

| Symbol | Meaning |
|---|---|
| `[ ]` | Not started |
| `[~]` | In progress |
| `[x]` | Done |

---

## Items

| # | Title | Area | Status |
|---|---|---|---|
| 1 | [Task Parameters Propagation](#1-task-parameters-propagation) | Core / Bug Fix | `[x]` |
| 2 | [Dockerize Application Services](#2-dockerize-application-services) | Infrastructure | `[x]` |
| 3 | [End-to-End Send-Email via Mailtrap](#3-end-to-end-send-email-via-mailtrap) | E2E Demo | `[x]` |
| 4 | [Job Dispatcher Resilience — No-Host Queuing](#4-job-dispatcher-resilience--no-host-queuing) | Reliability | `[x]` |
| 5 | [Task Type Discovery API](#5-task-type-discovery-api) | Developer Experience | `[x]` |
| 6 | [Structured Job Output](#6-structured-job-output) | Observability | `[x]` |
| 7 | [Trigger Config Encryption](#7-trigger-config-encryption) | Security | `[x]` |
| 8 | [Cron Deserialization Bug Fix](#8-cron-deserialization-bug-fix) | Bug Fix | `[x]` |
| 9 | [Disabled Automation Guard](#9-disabled-automation-guard) | Reliability | `[x]` |
| 10 | [SQL Trigger E2E Demo — ERP Inventory](#10-sql-trigger-e2e-demo--erp-inventory) | E2E Demo | `[x]` |
| 11 | [Docker Compose Service Restart Policy](#11-docker-compose-service-restart-policy) | Infrastructure | `[x]` |
| 12 | [Test Suite Repair and Expansion](#12-test-suite-repair-and-expansion) | Testing | `[x]` |

---

## 1. Task Parameters Propagation

### Problem

`WorkflowContext.Parameters` is always an **empty dictionary**. Every handler (`SendEmailHandler`, `HttpRequestHandler`, `RunScriptHandler`) calls `context.GetParameter<T>(key)` on startup, which throws `KeyNotFoundException` immediately. **No automation can execute any real work today.** This is the highest priority fix.

The root cause: there is no place on `Automation` or `Job` to store per-automation handler configuration. The `TaskId` field only identifies *which* handler to run; it says nothing about *how* to configure it.

### Design

Add a `TaskConfig` field to `Automation` (JSONB, nullable) that stores a flat `Dictionary<string, string>` of handler parameters. At job creation time, snapshot the config onto the `Job` entity so the WorkflowEngine always has the config that was active when the job was triggered.

**Domain changes:**
```csharp
// Automation.cs
public string? TaskConfig { get; private set; }  // jsonb, nullable

// Job.cs
public string? TaskConfig { get; private set; }  // jsonb, nullable — copied from Automation at creation
```

**Propagation chain:**
```
Automation.TaskConfig
  → AutomationTriggeredEvent.TaskConfig (string? = null)
  → Job.TaskConfig (stored in job DB — immutable after creation)
  → WorkflowEngine reads Job.TaskConfig, deserialises to Dictionary<string, JsonElement>
  → WorkflowContext.Parameters = deserialisedConfig
```

**WorkflowEngine change:**
```csharp
var parameters = job.TaskConfig is not null
    ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(job.TaskConfig) ?? []
    : new Dictionary<string, JsonElement>();

var context = new WorkflowContext
{
    JobId       = job.Id,
    TaskId      = job.TaskId,
    ConnectionId = connectionId,
    Parameters  = parameters
};
```

**API changes:**
- `CreateAutomationRequest` / `UpdateAutomationRequest`: add `string? TaskConfig = null`
- `AutomationResponse`: add `string? TaskConfig`
- Validation: if `TaskConfig` is non-null, validate it is parseable as a JSON object

**Migrations required:**
- `ALTER TABLE "Automations" ADD "TaskConfig" jsonb NULL`
- `ALTER TABLE "Jobs" ADD "TaskConfig" jsonb NULL` (in both job DBs)

### Files to Create / Modify

- `FlowForge.Domain/Entities/Automation.cs` — add `TaskConfig`
- `FlowForge.Domain/Entities/Job.cs` — add `TaskConfig`
- `FlowForge.Contracts/Events/AutomationTriggeredEvent.cs` — add `TaskConfig`
- `FlowForge.Infrastructure/Persistence/Platform/Configurations/AutomationConfiguration.cs`
- `FlowForge.Infrastructure/Persistence/Jobs/Configurations/JobConfiguration.cs`
- Platform migration + Designer + ModelSnapshot update
- Jobs migration + Designer + ModelSnapshot update (apply to both job DBs manually)
- `WebApi/DTOs/Requests/AutomationRequests.cs` — add `TaskConfig`
- `WebApi/DTOs/Responses/Responses.cs` — add `TaskConfig`
- `WebApi/Services/AutomationService.cs` — pass `TaskConfig` through
- `WebApi/Workers/AutomationTriggeredConsumer.cs` — pass `TaskConfig` to `Job.Create`
- `WorkflowEngine/Program.cs` — deserialise `job.TaskConfig` into `WorkflowContext.Parameters`
- `Contracts/Events/AutomationChangedEvent.cs` (AutomationSnapshot) — add `TaskConfig`
- `JobAutomator/Cache/AutomationCache.cs` (AutomationSnapshot) — add `TaskConfig`

---

## 2. Dockerize Application Services

### Problem

Only infrastructure (PostgreSQL, Redis, Jaeger) runs in Docker today. The five application services (`WebApi`, `JobAutomator`, `JobOrchestrator`, `WorkflowHost`, `WorkflowEngine`) run as bare `dotnet run` processes. This makes:
- E2E testing inconsistent across machines
- Deployment to any environment (staging, production, CI) impossible without manual steps
- The full system impossible to start with a single command

### Design

One `Dockerfile` per service, all using a two-stage build (SDK image for build, ASP.NET/runtime image for run). Add each service to `deploy/docker/compose.yaml` with proper depends_on, health checks, and environment variable configuration.

**Dockerfile pattern (multi-stage):**
```dockerfile
# Stage 1: build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY FlowForge.sln .
COPY src/ src/
RUN dotnet restore
RUN dotnet publish src/services/FlowForge.WebApi/FlowForge.WebApi.csproj \
    -c Release -o /app/publish

# Stage 2: runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "FlowForge.WebApi.dll"]
```

**WorkflowEngine** is special: it is spawned by WorkflowHost as a child process. Its binary must be available on the host's filesystem (or in the same container as WorkflowHost). Two options:
- Option A (recommended): include WorkflowEngine binary in the WorkflowHost image. `NativeProcessManager` resolves the path via a config value `WorkflowEngine:ExecutablePath`.
- Option B: use `DockerProcessManager` — each job runs WorkflowEngine in its own container. More isolation, more overhead.

**docker-compose additions:**
```yaml
services:
  webapi:
    build: { context: ../.., dockerfile: deploy/docker/Dockerfile.WebApi }
    ports: ["5015:8080"]
    environment:
      ConnectionStrings__DefaultConnection: "Host=flowforge-db-platform;..."
      Redis__ConnectionString: "flowforge-redis:6379"
    depends_on:
      flowforge-db-platform: { condition: service_healthy }
      flowforge-redis:        { condition: service_healthy }
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:8080/health/ready"]

  job-automator:
    build: { context: ../.., dockerfile: deploy/docker/Dockerfile.JobAutomator }
    environment: { ... }
    depends_on: { webapi: { condition: service_healthy } }

  job-orchestrator:
    build: { ... }
    environment: { ... }
    depends_on: { flowforge-db-platform: ..., flowforge-redis: ... }

  workflow-host:
    build: { ... }
    environment:
      NODE_NAME: "docker-host-1"
    depends_on: { job-orchestrator: { condition: service_healthy } }
```

**Key constraints:**
- Each service reads all config from environment variables (ASP.NET Core's `__` separator maps to `:` nesting)
- No secrets baked into images — all connection strings / keys via environment variables
- `depends_on` with `service_healthy` ensures startup order matches the system's boot sequence: databases → redis → webapi → automator/orchestrator → workflowhost

### Files to Create / Modify

- `deploy/docker/Dockerfile.WebApi`
- `deploy/docker/Dockerfile.JobAutomator`
- `deploy/docker/Dockerfile.JobOrchestrator`
- `deploy/docker/Dockerfile.WorkflowHost` (includes WorkflowEngine binary)
- `deploy/docker/compose.yaml` — add all five service containers
- `src/services/FlowForge.WorkflowHost/ProcessManagement/NativeProcessManager.cs` — make engine path configurable

---

## 3. End-to-End Send-Email via Mailtrap

### Problem

`SendEmailHandler` is a stub that sleeps for 1 second and returns success — it sends no email. To have a real E2E demo, we need:
1. Task parameters to flow into the handler (ROADMAP #1 must be done first)
2. A real SMTP implementation inside `SendEmailHandler`
3. A reachable SMTP relay for local development (Mailtrap via SMTP sandbox)
4. A concrete example automation to create and trigger

### Design

**SMTP client:** use `MailKit` (NuGet) — the standard .NET SMTP library. It supports TLS, AUTH LOGIN/PLAIN (required by Mailtrap), and is async-native.

**SmtpOptions:**
```csharp
public class SmtpOptions
{
    public const string SectionName = "Smtp";
    public string Host { get; init; } = "smtp.mailtrap.io";
    public int Port { get; init; } = 587;
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public string FromAddress { get; init; } = "noreply@flowforge.local";
    public string FromName { get; init; } = "FlowForge";
}
```

**Updated SendEmailHandler:**
```csharp
public class SendEmailHandler(SmtpOptions smtp, ILogger<SendEmailHandler> logger) : IWorkflowHandler
{
    public string TaskId => "send-email";

    public async Task<WorkflowResult> ExecuteAsync(WorkflowContext context, CancellationToken ct)
    {
        var to      = context.GetParameter<string>("to");
        var subject = context.GetParameter<string>("subject");
        var body    = context.GetParameter<string>("body");

        using var client = new SmtpClient();
        await client.ConnectAsync(smtp.Host, smtp.Port, SecureSocketOptions.StartTls, ct);
        await client.AuthenticateAsync(smtp.Username, smtp.Password, ct);

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(smtp.FromName, smtp.FromAddress));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body    = new TextPart("plain") { Text = body };

        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);

        logger.LogInformation("Email sent to {To}", to);
        return WorkflowResult.Success();
    }
}
```

**Mailtrap in docker-compose:** Mailtrap is a cloud service with a free SMTP sandbox tier. No local container needed — just configure the SMTP credentials from your Mailtrap inbox in the service's environment / appsettings. Alternatively, use `axllent/mailpit` (a self-hosted Mailtrap alternative) as a docker-compose service on port 1025 (SMTP) / 8025 (web UI).

**Example automation to create:**
```json
POST /api/automations
{
  "name": "Send Welcome Email",
  "taskId": "send-email",
  "hostGroupId": "<your-host-group-id>",
  "isEnabled": true,
  "conditionRoot": { "type": "trigger", "name": "manual-webhook" },
  "timeoutSeconds": 30,
  "maxRetries": 1,
  "taskConfig": {
    "to": "test@example.com",
    "subject": "Hello from FlowForge",
    "body": "This is a test email sent via the FlowForge workflow engine."
  }
}
```

Fire it:
```bash
POST /api/automations/{id}/triggers/manual-webhook/webhook
```

Watch the job progress via SignalR or `GET /api/jobs`.

### Files to Create / Modify

- `FlowForge.WorkflowEngine/FlowForge.WorkflowEngine.csproj` — add `MailKit`
- `FlowForge.WorkflowEngine/Options/SmtpOptions.cs` — new
- `FlowForge.WorkflowEngine/Handlers/SendEmailHandler.cs` — replace stub with real MailKit impl
- `FlowForge.WorkflowEngine/Program.cs` — register `SmtpOptions`, inject into handler
- `FlowForge.WorkflowEngine/appsettings.json` — add `Smtp` section
- `deploy/docker/compose.yaml` — optionally add Mailpit container

---

## 4. Job Dispatcher Resilience — No-Host Queuing

### Problem

When `JobDispatcherWorker` receives a `JobCreatedEvent` and finds no online hosts in the target host group, it logs a warning and continues. The job is XACK'd and stays in `Pending` status forever. There is no mechanism to dispatch it once hosts come back online.

This is particularly acute in development where you might start services in the wrong order, and in production during rolling restarts.

### Design

Add a **pending job scanner** to `JobOrchestrator` that periodically queries for jobs stuck in `Pending` status and re-publishes their `JobCreatedEvent` to trigger re-dispatch.

```csharp
public class PendingJobScannerWorker(
    IServiceProvider serviceProvider,
    IMessagePublisher publisher,
    IOptions<PendingJobScannerOptions> options,
    ILogger<PendingJobScannerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(_options.ScanIntervalSeconds), ct);
            await ScanAndRequeueAsync(ct);
        }
    }
}
```

`ScanAndRequeueAsync`:
1. For each configured job DB: query `Jobs WHERE Status = Pending AND CreatedAt < NOW() - interval`
2. For each such job: re-publish `JobCreatedEvent` (same fields as original)
3. Do not re-publish if `CreatedAt` is very recent (avoid racing with the initial dispatch)

**PendingJobScannerOptions:**
```csharp
public class PendingJobScannerOptions
{
    public const string SectionName = "PendingJobScanner";
    public int ScanIntervalSeconds { get; init; } = 30;
    public int StaleAfterSeconds   { get; init; } = 15;  // min age before re-queue
}
```

**Why re-publish to Redis rather than fixing JobDispatcherWorker in-place:** The dispatcher is event-driven; it only processes new stream entries. Re-publishing creates a new stream entry that will be processed on the next pass. This is simpler and more observable than adding a polling retry inside the dispatcher itself.

### Files to Create / Modify

- `FlowForge.JobOrchestrator/Options/PendingJobScannerOptions.cs` — new
- `FlowForge.JobOrchestrator/Workers/PendingJobScannerWorker.cs` — new
- `FlowForge.JobOrchestrator/Program.cs` — register options + hosted service
- `FlowForge.JobOrchestrator/appsettings.json` — add `PendingJobScanner` section

---

## 5. Task Type Discovery API

### Problem

There is no API endpoint listing the available workflow task types (`send-email`, `http-request`, `run-script`). A frontend or API client has no way to discover what `TaskId` values are valid or what parameters each task expects. This is the same problem solved for triggers by `ITriggerTypeDescriptor` — we need the same pattern for task handlers.

### Design

Mirror the trigger type descriptor pattern:

```csharp
public interface ITaskTypeDescriptor
{
    string TaskId { get; }
    string DisplayName { get; }
    string Description { get; }
    IReadOnlyList<TaskParameterField> Parameters { get; }
}

public record TaskParameterField(
    string Name,
    string Label,
    string Type,        // "text", "textarea", "number", "boolean"
    bool Required,
    string? DefaultValue = null,
    string? HelpText = null);
```

**Built-in descriptors:**
- `SendEmailTaskDescriptor` — parameters: `to` (text, required), `subject` (text, required), `body` (textarea, required)
- `HttpRequestTaskDescriptor` — parameters: `url` (text, required), `method` (text, default `"GET"`)
- `RunScriptTaskDescriptor` — parameters: `interpreter` (text, required), `scriptPath` (text, required), `arguments` (text, optional)

**New endpoint:**
```
GET /api/task-types
```
Returns `ITaskTypeDescriptor[]`. No auth required (public discovery).

**Where to register:** `ITaskTypeDescriptor` implementations can live in `FlowForge.WorkflowEngine` (alongside the handlers) or in a shared location if multiple services need them. Since only the WebApi serves the endpoint, the descriptors should be registered there — either co-located or imported from a shared assembly.

### Files to Create / Modify

- `FlowForge.Infrastructure` or `FlowForge.Contracts` — `ITaskTypeDescriptor` interface
- `FlowForge.WebApi/TaskTypes/SendEmailTaskDescriptor.cs` (+ Http + RunScript)
- `FlowForge.WebApi/Controllers/TaskTypesController.cs` — `GET /api/task-types`
- `FlowForge.WebApi/Program.cs` — register descriptors

---

## 6. Structured Job Output

### Problem

`WorkflowContext.Outputs` (`Dictionary<string, object>`) is populated by handlers during execution (e.g. `HttpRequestHandler` stores `responseBody`) but the data is never persisted — it lives only in memory for the duration of the process and is discarded on exit. There is no way to see job results after the fact.

### Design

Add an `OutputJson` field to `Job` (JSONB, nullable). After `handler.ExecuteAsync` returns successfully, the WorkflowEngine serialises `context.Outputs` and includes it in the final `JobStatusChangedEvent`. `JobStatusChangedConsumer` in WebApi stores it on the job entity.

**Domain change:**
```csharp
// Job.cs
public string? OutputJson { get; private set; }

public void SetOutput(string? outputJson)
{
    OutputJson = outputJson;
    UpdatedAt  = DateTimeOffset.UtcNow;
}
```

**WorkflowEngine change:**
```csharp
var outputJson = context.Outputs.Count > 0
    ? JsonSerializer.Serialize(context.Outputs)
    : null;

await reporter.ReportStatusAsync(jobId, automationId, connectionId, finalStatus,
    result.Message, outputJson, CancellationToken.None);
```

**Event change:**
```csharp
record JobStatusChangedEvent(
    Guid JobId, Guid AutomationId, string ConnectionId,
    JobStatus Status, string? Message, DateTimeOffset UpdatedAt,
    string? OutputJson = null);   // new
```

**JobStatusChangedConsumer:**
```csharp
if (@event.OutputJson is not null)
    job.SetOutput(@event.OutputJson);
```

**API:**
Add `OutputJson: string?` to `JobResponse`. Clients can deserialise the JSON for task-specific result parsing.

**Migration:**
- `ALTER TABLE "Jobs" ADD "OutputJson" jsonb NULL` (in both job DBs)

### Files to Create / Modify

- `FlowForge.Domain/Entities/Job.cs` — add `OutputJson`, `SetOutput`
- `FlowForge.Contracts/Events/JobStatusChangedEvent.cs` — add `OutputJson`
- `FlowForge.Infrastructure/Persistence/Jobs/Configurations/JobConfiguration.cs`
- Jobs migration + Designer + ModelSnapshot (apply to both job DBs)
- `FlowForge.WorkflowEngine/Reporting/JobProgressReporter.cs` — accept + forward `outputJson`
- `FlowForge.WorkflowEngine/Program.cs` — serialise `context.Outputs` after handler returns
- `WebApi/Workers/JobStatusChangedConsumer.cs` — call `job.SetOutput`
- `WebApi/DTOs/Responses/Responses.cs` — add `OutputJson` to `JobResponse`

---

## Implementation Order

The items have the following hard dependencies:

```
#1 (Task Parameters) ──► #3 (Send-Email E2E)
#2 (Dockerize)       ──► #3 (consistent E2E environment)
#1 + #2 + #3         = working E2E demo
#4, #5, #6           = independent — can be done in any order after #1
#7, #8, #9, #10, #11, #12 = all completed independently
```

---

## 7. Trigger Config Encryption

AES-256-GCM field-level encryption for sensitive trigger config values (e.g. SQL `connectionString`).

- `IEncryptionService` / `AesEncryptionService` in `FlowForge.Infrastructure/Encryption/`
- Platform master key from `FlowForge:EncryptionKey` config (base64 32-byte). Dev fallback: all-zeros key with loud logger warning.
- Storage format: `enc:v1:<base64(nonce||ciphertext||tag)>` — 12-byte nonce, 128-bit auth tag
- `ITriggerTypeDescriptor.GetSensitiveFieldNames()` — default interface method, returns `[]`. Overridden by `SqlTriggerDescriptor` to return `["connectionString"]`
- `AutomationService`: encrypts sensitive fields on `CreateAsync`/`UpdateAsync`; redacts to `***` in all API responses; passes encrypted `configJson` in snapshots
- `SqlTriggerEvaluator`: decrypts `connectionString` at evaluation time before connecting
- `AddEncryption()` extension method registers the service; called in `WebApi` and `JobAutomator` `Program.cs`

---

## 8. Cron Deserialization Bug Fix

`System.Text.Json` is case-sensitive by default. Trigger `configJson` is written as camelCase JSON (`cronExpression`) but the C# records used for deserialization have PascalCase properties (`CronExpression`). This caused `QuartzScheduleSync.SyncAsync` to silently skip `ScheduleJob` calls — the Quartz table was always empty and no schedule triggers ever fired.

Fix: `JsonSerializerOptions` with `PropertyNameCaseInsensitive = true` added to all trigger descriptor `ValidateConfig` methods and `QuartzScheduleSync.SyncAsync`. A compile-time ambiguity between `Quartz.JsonSerializerOptions` and `System.Text.Json.JsonSerializerOptions` in `QuartzScheduleSync.cs` was resolved with full qualification.

---

## 9. Disabled Automation Guard

`AutomationTriggeredConsumer` had no `IsEnabled` check. Events already queued in Redis before an automation was disabled would still create jobs.

Fix: After loading the automation from the database, the consumer now checks `if (!automation.IsEnabled)` and drops the event (`continue`) before any job creation logic runs. This is a last-line-of-defence check — the JobAutomator cache already skips disabled automations, but in-flight events bypass the cache.

---

## 10. SQL Trigger E2E Demo — ERP Inventory

Added a self-contained ERP inventory demo environment:

- `deploy/docker/postgres-erp-test` — PostgreSQL container on port 5456, `erp_inventory` DB
- `deploy/docker/erp-test-init.sql` — creates `locations` and `products` tables; seeds 5 products all with `quantity > 5`
- `deploy/docker/erp-automation-payload.json` — ready-to-use payload for creating the ERP low-stock automation via `curl -d @erp-automation-payload.json`
- SQL trigger query: `SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END FROM products WHERE quantity < reorder_threshold`
- Task: `run-script` → `send_email.py` via Resend API; fires when a product's `quantity` is manually lowered below its `reorder_threshold`

---

## 11. Docker Compose Service Restart Policy

All five application services (`webapi`, `job-automator`, `job-orchestrator`, `workflowhost-minion`, `workflowhost-titan`) now have `restart: unless-stopped`. Previously, a transient crash (e.g. the orchestrator hitting a missing-table error during a WebApi restart window) would leave the container exited with no recovery — jobs would pile up in `Pending` indefinitely.

---

## 12. Test Suite Repair and Expansion

**Pre-existing failures fixed:**
- `AutomationServiceTests`: `AutomationService` constructor gained `IEncryptionService` — tests updated with a pass-through stub
- `AutomationServiceTests`: `NSubstitute.AmbiguousArgumentsException` from `ci.Arg<string>()` with multiple string args — changed to `ci.ArgAt<string>(0)`
- `JobStatusChangedConsumerTests`: EF Core double-tracking — the consumer loaded a tracked `Job` instance; tests then called `Entry(job).ReloadAsync()` on the same context with a detached instance. Fixed by querying `Jobs.FindAsync(job.Id)` instead

**New tests added (integration tests: 40/40):**
- `Unit/AesEncryptionServiceTests.cs` — 9 tests: encrypt/decrypt roundtrip, `enc:v1:` prefix, nonce randomness, `IsEncrypted`, plaintext pass-through, tamper detection, invalid key length
- `Unit/TriggerDescriptorTests.cs` — 9 tests: camelCase validation fix (the root cause of the cron bug), `GetSensitiveFieldNames`, all validation edge cases for `schedule` and `sql` descriptors
- `Workers/AutomationTriggeredConsumerTests.cs` +1: `WhenAutomationIsDisabled_DropsEventAndCreatesNoJob`
- `Services/AutomationServiceTests.cs` +2: `CreateAutomation_SqlTrigger_StoresEncryptedConnectionStringInDb`, `GetAutomation_SqlTrigger_RedactsConnectionStringInResponse`
