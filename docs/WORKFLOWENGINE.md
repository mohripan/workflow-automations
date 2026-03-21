# WORKFLOWENGINE.md — Workflow Engine Service

## Responsibility

The Workflow Engine is a **single-job console application** spawned by `WorkflowHost` as a child process. It executes exactly one `Job` per process lifetime — from seconds to many hours — then exits cleanly.

One engine process = one job. There is no multiplexing.

---

## Lifecycle

```
WorkflowHost spawns WorkflowEngine
          │
          ▼
Read JOB_ID, JOB_AUTOMATION_ID, CONNECTION_ID from environment
          │
          ▼
Load Job from job DB (keyed by CONNECTION_ID)
          │
          ▼
Optionally create linked timeout CancellationTokenSource
          │
          ▼
Start heartbeat loop (every 5 s → redis.SetAsync "heartbeat:{jobId}")
          │
          ▼
ReportStatus → InProgress
          │
          ▼
Deserialize job.TaskConfig → WorkflowContext.Parameters
          │
          ▼
Build WorkflowContext { JobId, TaskId, ConnectionId, Parameters }
          │
          ▼
WorkflowHandlerRegistry.Get(job.TaskId) → IWorkflowHandler
          │
          ▼
handler.ExecuteAsync(context, cts.Token)
          │
     ┌────┴─────────────────────────────────────┐
     │                                          │
   Normal exit                       OperationCanceledException
     │                                          │
   Record JobDurationSeconds          timeout? → Error "timed out"
   Serialize context.Outputs          otherwise → Cancelled
   Map WorkflowResult → JobStatus    ReportStatus(Error|Cancelled)
   ReportStatus(finalStatus, outputJson)
     │
   Exit 0 / 1
```

---

## Environment Variables

| Variable | Description |
|---|---|
| `JOB_ID` | `Guid` of the job to execute |
| `JOB_AUTOMATION_ID` | `Guid` of the owning automation (for status reporting) |
| `CONNECTION_ID` | Key into `JobConnections` config to load the correct job DB |

All other configuration (Redis, PostgreSQL connections, OTLP endpoint, SMTP credentials) is inherited from the parent WorkflowHost process environment.

---

## Job Timeout Enforcement

If `job.TimeoutSeconds` is set, a linked `CancellationTokenSource` is created:

```csharp
if (job.TimeoutSeconds.HasValue)
{
    timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(job.TimeoutSeconds.Value));
    cts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeoutCts.Token);
}
```

In the `catch (OperationCanceledException)` block:
```csharp
if (timeoutCts?.IsCancellationRequested == true)
{
    // Timeout path → Error status
    await reporter.ReportStatusAsync(..., JobStatus.Error, $"Job timed out after {timeoutSeconds} seconds.");
    return 1;
}
// Otherwise Ctrl+C / WorkflowHost shutdown → Cancelled status
await reporter.ReportStatusAsync(..., JobStatus.Cancelled, "Cancelled");
return 0;
```

---

## IWorkflowHandler

```csharp
public interface IWorkflowHandler
{
    string TaskId { get; }
    Task<WorkflowResult> ExecuteAsync(WorkflowContext context, CancellationToken ct);
}
```

Handlers are registered as `IEnumerable<IWorkflowHandler>` in the DI container and keyed by `TaskId` in `WorkflowHandlerRegistry`. Resolved by `registry.Get(job.TaskId)`. Unknown `TaskId` → `KeyNotFoundException` → job reported as `Error`.

---

## WorkflowContext

```csharp
public sealed class WorkflowContext
{
    public Guid JobId { get; init; }
    public string TaskId { get; init; }
    public string ConnectionId { get; init; }
    public Dictionary<string, JsonElement> Parameters { get; init; }  // from job.TaskConfig
    public Dictionary<string, object> Outputs { get; }                // handler stores results here

    public T GetParameter<T>(string key);   // throws KeyNotFoundException if missing
}
```

`Parameters` is populated by deserializing `job.TaskConfig` (a JSON object stored on the job). If `TaskConfig` is null, `Parameters` is an empty dictionary.

---

## WorkflowResult

```csharp
public record WorkflowResult(WorkflowResultStatus Status, string? Message = null)
{
    public static WorkflowResult Success(string? message = null) => new(WorkflowResultStatus.Completed, message);
    public static WorkflowResult Failure(string msg)             => new(WorkflowResultStatus.Failed, msg);
    public static WorkflowResult Cancellation()                  => new(WorkflowResultStatus.Cancelled);
    public static WorkflowResult Fault(string msg)               => new(WorkflowResultStatus.Error, msg);
}
```

### Status Mapping

| `WorkflowResultStatus` | `JobStatus` |
|---|---|
| `Completed` | `Completed` |
| `Failed` | `CompletedUnsuccessfully` |
| `Cancelled` | `Cancelled` |
| `Error` | `Error` |

`CompletedUnsuccessfully` means the handler ran to completion but determined the outcome was a failure (e.g. non-2xx HTTP response). `Error` means an unhandled exception or timeout.

---

## Built-in Handlers

### SendEmailHandler (`"send-email"`)
Sends email via SMTP using `System.Net.Mail.SmtpClient`. Reads credentials from `SmtpOptions`.

Parameters (from `WorkflowContext.Parameters`):
- `"to"` — recipient address (required)
- `"subject"` — email subject line (required)
- `"body"` — email body, plain text (required)
- `"from"` — override sender address (optional; falls back to `SmtpOptions.DefaultFromAddress`)

### HttpRequestHandler (`"http-request"`)
Parameters:
- `"url"` — target URL (required)
- `"method"` — HTTP method (optional, default: `"GET"`)

Makes the HTTP request via `IHttpClientFactory`. Returns `Failure` on non-2xx response. Stores response body in `context.Outputs["responseBody"]`.

### RunScriptHandler (`"run-script"`)
Parameters:
- `"interpreter"` — executable path (required, e.g. `"python"`, `"bash"`)
- `"scriptPath"` — path to the script file (required)
- `"arguments"` — optional arguments string

Spawns a child process, awaits exit. Returns `Failure` with stderr output on non-zero exit code.

---

## Structured Outputs

After `handler.ExecuteAsync` returns, the engine serializes `context.Outputs` and passes it to the final `ReportStatusAsync` call:

```csharp
var outputJson = context.Outputs.Count > 0
    ? JsonSerializer.Serialize(context.Outputs)
    : null;

await reporter.ReportStatusAsync(jobId, automationId, connectionId, finalStatus, result.Message, outputJson, CancellationToken.None);
```

`outputJson` is included in `JobStatusChangedEvent` and persisted to `Job.OutputJson` (jsonb) by `JobStatusChangedConsumer`. Clients can read it from `GET /api/jobs/{id}` → `JobResponse.OutputJson`.

---

## IJobReporter

```csharp
public interface IJobReporter
{
    Task ReportStatusAsync(Guid jobId, Guid automationId, string connectionId,
                           JobStatus status, string? message = null,
                           string? outputJson = null, CancellationToken ct = default);
    Task RefreshHeartbeatAsync(Guid jobId, CancellationToken ct = default);
}
```

`JobProgressReporter` implementation:
- `ReportStatusAsync` → publishes `JobStatusChangedEvent` (including `OutputJson`) to Redis via `IMessagePublisher`
- `RefreshHeartbeatAsync` → `redis.SetAsync($"heartbeat:{jobId}", "alive", TimeSpan.FromSeconds(30))`

The heartbeat is refreshed every 5 seconds in a background `Task.Run` loop that runs for the lifetime of the job.

---

## SmtpOptions

```csharp
public class SmtpOptions
{
    public const string SectionName = "Smtp";
    public string Host { get; init; } = "sandbox.smtp.mailtrap.io";
    public int Port { get; init; } = 2525;
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public bool EnableSsl { get; init; } = true;
    public string DefaultFromAddress { get; init; } = "noreply@flowforge.io";
}
```

All SMTP settings are read from configuration (environment variables in Docker). No code change is needed to switch SMTP providers — only configuration.

---

## Metrics

`FlowForgeMetrics.JobDurationSeconds` is recorded after `handler.ExecuteAsync` returns:

```csharp
var sw = Stopwatch.StartNew();
var result = await handler.ExecuteAsync(context, cts.Token);
sw.Stop();

FlowForgeMetrics.JobDurationSeconds.Record(
    sw.Elapsed.TotalSeconds,
    new KeyValuePair<string, object?>("task_id", job.TaskId));
```

---

## Adding a New Handler

1. Create `MyHandler : IWorkflowHandler` in `FlowForge.WorkflowEngine/Handlers/`
2. Set `TaskId` to a unique string (e.g. `"my-task"`)
3. Read parameters via `context.GetParameter<T>(key)`
4. Optionally write results to `context.Outputs["myKey"] = value`
5. Register in `Program.cs`: `builder.Services.AddScoped<IWorkflowHandler, MyHandler>()`
6. Add a corresponding `ITaskTypeDescriptor` in `FlowForge.Infrastructure/Tasks/Descriptors/` and register it in `ServiceCollectionExtensions.AddTaskTypeRegistry()` so it appears in `GET /api/task-types`

---

## DI Registration (Program.cs, abbreviated)

```csharp
builder.Services.AddInfrastructure(builder.Configuration, "WorkflowEngine");
builder.Services.AddHttpClient();

builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.SectionName));

builder.Services.AddSingleton<WorkflowHandlerRegistry>();
builder.Services.AddScoped<IWorkflowHandler, SendEmailHandler>();
builder.Services.AddScoped<IWorkflowHandler, HttpRequestHandler>();
builder.Services.AddScoped<IWorkflowHandler, RunScriptHandler>();

builder.Services.AddScoped<IJobReporter, JobProgressReporter>();
```
