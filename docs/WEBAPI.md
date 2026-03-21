# WEBAPI.md — Web API Service

## Responsibility

The Web API is an ASP.NET Core 10 application. Its responsibilities are:
1. Serve the REST API for managing Automations, Triggers, Jobs, Host Groups, and Hosts
2. Push real-time job status updates to UI clients via SignalR
3. Run background workers: `AutomationTriggeredConsumer`, `JobStatusChangedConsumer`, `OutboxRelayWorker`
4. Expose the Dead Letter Queue management API (`DlqController`)
5. Handle incoming webhooks that fire automation triggers

---

## Endpoints Overview

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/automations` | List all automations |
| `POST` | `/api/automations` | Create automation |
| `GET` | `/api/automations/{id}` | Get automation |
| `PUT` | `/api/automations/{id}` | Update automation |
| `DELETE` | `/api/automations/{id}` | Delete automation |
| `POST` | `/api/automations/{id}/enable` | Enable automation |
| `POST` | `/api/automations/{id}/disable` | Disable automation |
| `GET` | `/api/automations/{id}/triggers` | List triggers |
| `POST` | `/api/automations/{id}/triggers` | Add trigger |
| `PUT` | `/api/automations/{id}/triggers/{triggerId}` | Update trigger |
| `DELETE` | `/api/automations/{id}/triggers/{triggerId}` | Remove trigger |
| `POST` | `/api/automations/{id}/triggers/{name}/webhook` | Fire webhook trigger |
| `GET` | `/api/triggers/types` | List trigger type descriptors |
| `GET` | `/api/triggers/types/{typeId}` | Get single trigger type descriptor |
| `POST` | `/api/triggers/types/{typeId}/validate-config` | Validate trigger config JSON |
| `GET` | `/api/task-types` | List task type descriptors |
| `GET` | `/api/task-types/{taskId}` | Get single task type descriptor |
| `GET` | `/api/jobs` | List jobs |
| `GET` | `/api/jobs/{id}` | Get job |
| `POST` | `/api/jobs/{id}/cancel` | Cancel job |
| `DELETE` | `/api/jobs/{id}` | Remove job |
| `GET` | `/api/host-groups` | List host groups |
| `POST` | `/api/host-groups` | Create host group |
| `GET` | `/api/host-groups/{id}/hosts` | List hosts in group |
| `GET` | `/api/dlq` | List DLQ entries |
| `DELETE` | `/api/dlq/{id}` | Delete DLQ entry |
| `POST` | `/api/dlq/{id}/replay` | Replay DLQ entry to source stream |

---

## AutomationService

Handles all automation and trigger CRUD. Key behaviours:
- `CreateAsync` / `UpdateAsync`: calls `EncryptSensitiveFields(typeId, configJson)` on each trigger before storage. Fields declared by `ITriggerTypeDescriptor.GetSensitiveFieldNames()` (e.g. SQL `connectionString`) are AES-256-GCM encrypted. Writes `AutomationChangedEvent` to outbox atomically with `SaveAsync`.
- `DeleteAsync` cascade-deletes triggers and writes `AutomationChangedEvent` (type `Deleted`).
- `MapToResponse`: calls `RedactSensitiveFields(typeId, configJson)` — sensitive fields become `"***"` in all API responses. Clients never see encrypted ciphertext or raw secrets.
- `MapToSnapshotAsync`: carries the **encrypted** `configJson` into the outbox snapshot. Evaluators (in JobAutomator) decrypt at evaluation time.
- `AddTriggerAsync` / `UpdateTriggerAsync` / `RemoveTriggerAsync` each write `AutomationChangedEvent` (type `Updated`) so the JobAutomator cache stays current.

`AutomationService` depends on `IEncryptionService` (injected via constructor).

---

## DTOs

### CreateAutomationRequest / UpdateAutomationRequest
```csharp
public record CreateAutomationRequest(
    string Name,
    string? Description,
    string TaskId,
    Guid HostGroupId,
    bool IsEnabled,
    string ConditionRoot,          // JSON string of TriggerConditionNode tree
    int? TimeoutSeconds = null,    // null = no timeout
    int MaxRetries = 0,            // 0 = no retry
    string? TaskConfig = null);    // JSON object of handler parameters

public record UpdateAutomationRequest(
    string Name,
    string? Description,
    string TaskId,
    Guid HostGroupId,
    bool IsEnabled,
    string ConditionRoot,
    int? TimeoutSeconds = null,
    int MaxRetries = 0,
    string? TaskConfig = null);
```

### AutomationResponse
```csharp
public record AutomationResponse(
    Guid Id,
    string Name,
    string? Description,
    string TaskId,
    Guid HostGroupId,
    bool IsEnabled,
    int? TimeoutSeconds,
    int MaxRetries,
    string? TaskConfig,
    List<TriggerResponse> Triggers,
    TriggerConditionResponse TriggerCondition,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
```

### CreateTriggerRequest / TriggerResponse
```csharp
public record CreateTriggerRequest(string Name, string TypeId, string ConfigJson);

public record TriggerResponse(
    Guid Id, string Name, string TypeId, string ConfigJson,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
```

### JobResponse
```csharp
public record JobResponse(
    Guid Id,
    Guid AutomationId,
    string AutomationName,
    Guid HostGroupId,
    Guid? HostId,
    JobStatus Status,
    string? Message,
    string? OutputJson,    // serialized context.Outputs; null until job completes
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
```

---

## Background Workers

### AutomationTriggeredConsumer

Consumes `AutomationTriggeredEvent` from `flowforge:automation-triggered`.

1. Load `Automation` from the database (re-fetch — not from cache)
2. **IsEnabled guard:** if `automation.IsEnabled == false` → log and `continue`. This is a last-line-of-defence check against events already in-flight when an automation is disabled
3. Load `HostGroup`; resolve `IJobRepository` by `ConnectionId`
4. **Duplicate prevention:** if `automation.ActiveJobId` is set, check that job's status. If still active → skip. If terminal or missing → `automation.ClearActiveJob()` and continue
5. `Job.Create(automationId, taskId, connectionId, hostGroupId, triggeredAt, timeoutSeconds, retryAttempt, maxRetries, taskConfig)` — `taskConfig` is snapshotted from the event
4. `automation.SetActiveJob(job.Id)`
5. Write `JobCreatedEvent` to outbox
6. `automationRepo.SaveAsync(automation)` — commits automation update + outbox in one transaction
7. `jobRepo.SaveAsync(job)`

Records `FlowForgeMetrics.JobsCreated` (tags: `automation_id`, `host_group_id`).

On exception → `IDlqWriter.WriteAsync` + continue.

### JobStatusChangedConsumer

Consumes `JobStatusChangedEvent` from `flowforge:job-status-changed`.

1. Load job, call `job.Transition(@event.Status)`, optionally `job.SetMessage(@event.Message)`, and optionally `job.SetOutput(@event.OutputJson)`
2. `jobRepo.SaveAsync(job)`
3. Record `FlowForgeMetrics.JobsCompleted` or `FlowForgeMetrics.JobsFailed` for terminal statuses
4. If status `IsTerminal()`:
   - Load `Automation`; call `automation.ClearActiveJob()`
   - If status is `Error` or `CompletedUnsuccessfully` **and** `job.RetryAttempt < job.MaxRetries`:
     - Write `AutomationTriggeredEvent` to outbox with `RetryAttempt = job.RetryAttempt + 1`
   - `automationRepo.SaveAsync(automation)` — commits `ClearActiveJob` + retry outbox atomically
5. Push `JobStatusUpdate` to SignalR group `"job:{job.Id}"`

On exception → `IDlqWriter.WriteAsync` + continue.

### OutboxRelayWorker

Polls `OutboxMessages` where `SentAt IS NULL`, ordered by `CreatedAt`, limited to `OutboxRelayOptions.BatchSize` (default: 50). For each message, calls `StreamAddAsync(msg.StreamName, "payload", msg.Payload)` then `msg.MarkSent()`. Commits all marks in one `SaveChangesAsync`. Sleeps for `OutboxRelayOptions.PollIntervalMs` ms (default: 500) between passes.

```json
// appsettings.json
"OutboxRelay": {
  "PollIntervalMs": 500,
  "BatchSize": 50
}
```

---

## DlqController

`GET /api/dlq?limit=50` — reads up to `limit` entries from `flowforge:dlq` via `XRANGE`.

`DELETE /api/dlq/{id}` — removes entry via `XDEL`.

`POST /api/dlq/{id}/replay` — reads the entry, re-publishes the original `payload` to the entry's `sourceStream` via `XADD`, then returns `202 Accepted`.

> Replay re-adds the raw JSON payload to the source stream. The consumer group will pick it up as a new message. Use with care — the original cause of failure must be resolved first.

---

## SignalR Hub

```csharp
public interface IJobStatusClient
{
    Task OnJobStatusChanged(JobStatusUpdate update);
}

public class JobStatusHub : Hub<IJobStatusClient>
{
    public async Task JoinJobGroup(string jobId) =>
        await Groups.AddToGroupAsync(Context.ConnectionId, $"job:{jobId}");

    public async Task LeaveJobGroup(string jobId) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"job:{jobId}");
}
```

Hub URL: `ws://.../hubs/job-status`. Clients call `JoinJobGroup(jobId)` to subscribe to updates for a specific job.

---

## Validation

`FluentValidation` is wired via `AddFluentValidationAutoValidation()`. Validators live in `WebApi/Validators/`. Currently: `CreateAutomationRequestValidator` (validates `Name` length, `TaskId` not empty, `MaxRetries >= 0`, `TimeoutSeconds > 0 if provided`).

---

## Exception Handling Middleware

`ExceptionHandlingMiddleware` catches unhandled exceptions and returns structured JSON:

| Exception type | HTTP status |
|---|---|
| `AutomationNotFoundException` | 404 |
| `InvalidAutomationException` | 400 |
| `InvalidJobTransitionException` | 409 |
| `DomainException` (base) | 400 |
| anything else | 500 |

---

## Webhook Flow

1. `POST /api/automations/{id}/webhook` with optional `X-Webhook-Secret` header
2. `AutomationService.FireWebhookAsync`:
   - Throws `InvalidAutomationException` if automation is disabled
   - Finds the webhook trigger on the automation
   - If `config.SecretHash` is set: requires `X-Webhook-Secret` header; verifies with `BCrypt.Net.BCrypt.Verify`. Throws `UnauthorizedWebhookException` on mismatch or missing header
3. On validation pass: `redis.SetAsync($"trigger:webhook:{triggerId}:fired", "1", TimeSpan.FromMinutes(10))`
4. JobAutomator's `WebhookTriggerEvaluator` reads and clears this key on its next evaluation pass

---

## Health Checks

```
GET /health/live   → 200 always (liveness)
GET /health/ready  → checks PostgreSQL (platform DB) + Redis
```

---

## Configuration

```json
{
  "OutboxRelay": {
    "PollIntervalMs": 500,
    "BatchSize": 50
  },
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=flowforge_platform;Username=postgres;Password=postgres"
  },
  "JobConnections": {
    "wf-jobs-minion": { "ConnectionString": "...", "Provider": "PostgreSQL" },
    "wf-jobs-titan":  { "ConnectionString": "...", "Provider": "PostgreSQL" }
  },
  "Redis": { "ConnectionString": "localhost:6379" },
  "AllowedOrigins": ["http://localhost:3000"],
  "OpenTelemetry": { "OtlpEndpoint": "http://localhost:4317" },
  "FlowForge": {
    "EncryptionKey": "<base64-encoded 32-byte AES key>"
  }
}
```

---

## DI Registration (Program.cs, abbreviated)

```csharp
builder.Services.AddInfrastructure(builder.Configuration, "WebApi");
builder.Services.AddEncryption();  // IEncryptionService / AesEncryptionService
builder.Services.Configure<OutboxRelayOptions>(
    builder.Configuration.GetSection(OutboxRelayOptions.SectionName));

builder.Services.AddScoped<IAutomationService, AutomationService>();
builder.Services.AddScoped<IJobService, JobService>();
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssemblyContaining<CreateAutomationRequestValidator>();

builder.Services.AddHostedService<AutomationTriggeredConsumer>();
builder.Services.AddHostedService<JobStatusChangedConsumer>();
builder.Services.AddHostedService<OutboxRelayWorker>();
```
