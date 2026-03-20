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
Build WorkflowContext { JobId, TaskId, ConnectionId, Parameters = {} }
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
   Map WorkflowResult → JobStatus    otherwise → Cancelled
   ReportStatus(finalStatus)         ReportStatus(Error|Cancelled)
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

All other configuration (Redis, PostgreSQL connections, OTLP endpoint) is inherited from the parent WorkflowHost process environment.

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
    public IReadOnlyDictionary<string, JsonElement> Parameters { get; init; }
    public Dictionary<string, object> Outputs { get; }   // handler can store outputs here

    public T GetParameter<T>(string key);   // throws KeyNotFoundException if missing
}
```

> **⚠️ Known Issue:** `Parameters` is currently always an **empty dictionary**. Handlers that call `context.GetParameter<T>(key)` will throw `KeyNotFoundException` at runtime. This is ROADMAP item #1 — Task Parameters Propagation. Until implemented, `SendEmailHandler`, `HttpRequestHandler`, and `RunScriptHandler` all fail immediately when invoked.

---

## WorkflowResult

```csharp
public record WorkflowResult(WorkflowResultStatus Status, string? Message = null)
{
    public static WorkflowResult Success()            => new(WorkflowResultStatus.Completed);
    public static WorkflowResult Failure(string msg)  => new(WorkflowResultStatus.Failed, msg);
    public static WorkflowResult Cancellation()       => new(WorkflowResultStatus.Cancelled);
    public static WorkflowResult Fault(string msg)    => new(WorkflowResultStatus.Error, msg);
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
Expected parameters (from `WorkflowContext.Parameters`):
- `"to"` — recipient address
- `"subject"` — email subject line
- `"body"` — email body

**Current state:** stub only — simulates a 1-second delay and returns success. No real SMTP. See ROADMAP item #3.

### HttpRequestHandler (`"http-request"`)
Expected parameters:
- `"url"` — target URL
- `"method"` — HTTP method (default: `"GET"`)

Makes the HTTP request via `IHttpClientFactory`. Returns `Failure` on non-2xx response. Stores response body in `context.Outputs["responseBody"]`.

**Current state:** functional but `Parameters` is always empty (see above).

### RunScriptHandler (`"run-script"`)
Expected parameters:
- `"interpreter"` — executable path (default: `"cmd.exe"`)
- `"scriptPath"` — path to the script file
- `"arguments"` — optional arguments string

Spawns a child process, awaits exit. Returns `Failure` with stderr output on non-zero exit code.

**Current state:** functional but `Parameters` is always empty (see above).

---

## IJobReporter

```csharp
public interface IJobReporter
{
    Task ReportStatusAsync(Guid jobId, Guid automationId, string connectionId,
                           JobStatus status, string? message = null, CancellationToken ct = default);
    Task RefreshHeartbeatAsync(Guid jobId, CancellationToken ct = default);
}
```

`JobProgressReporter` implementation:
- `ReportStatusAsync` → publishes `JobStatusChangedEvent` to Redis via `IMessagePublisher`
- `RefreshHeartbeatAsync` → `redis.SetAsync($"heartbeat:{jobId}", "alive", TimeSpan.FromSeconds(30))`

The heartbeat is refreshed every 5 seconds in a background `Task.Run` loop that runs for the lifetime of the job.

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
3. Read parameters via `context.GetParameter<T>(key)` (works once ROADMAP #1 is implemented)
4. Register in `Program.cs`: `builder.Services.AddScoped<IWorkflowHandler, MyHandler>()`

---

## DI Registration (Program.cs, abbreviated)

```csharp
builder.Services.AddInfrastructure(builder.Configuration, "WorkflowEngine");
builder.Services.AddHttpClient();

builder.Services.AddSingleton<WorkflowHandlerRegistry>();
builder.Services.AddScoped<IWorkflowHandler, SendEmailHandler>();
builder.Services.AddScoped<IWorkflowHandler, HttpRequestHandler>();
builder.Services.AddScoped<IWorkflowHandler, RunScriptHandler>();
builder.Services.AddScoped<IJobReporter, JobProgressReporter>();
```
