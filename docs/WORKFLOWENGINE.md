# WORKFLOWENGINE.md — Workflow Engine Service

## Responsibility

The Workflow Engine is a **single-job Console Application** spawned by `WorkflowHost` as a child process. It executes exactly **one Job** per process lifetime — which can range from seconds to many hours depending on the job — then exits cleanly.

It is responsible for:
1. Loading the job definition from the database on startup (using `JOB_ID` + `CONNECTION_ID` from env vars)
2. Resolving the correct `IWorkflowHandler` for the job's `TaskId`
3. Executing the handler
4. Reporting progress and status changes to Redis Streams throughout execution
5. Writing a heartbeat key to Redis every 5 seconds while running
6. Responding to cancellation gracefully before exiting

> **"Single-job" not "short-lived"** — a job might run for 5 minutes or 5 hours. The process lives exactly as long as the job does. One process = one job, always.

---

## Lifecycle

```
WorkflowHost spawns WorkflowEngine
          │  (env: JOB_ID, CONNECTION_ID, REDIS_CONNECTION, DB_CONNECTION)
          ▼
[Startup] Resolve JOB_ID + CONNECTION_ID from env
          │
          ▼
[DB]    Load Job from the correct host-group database (via CONNECTION_ID)
          │
          ▼
[Publish] JobStatusChangedEvent → InProgress
          │
          │   ┌──────────────────────────────────────┐
          │   │  Heartbeat timer (every 5s, TTL 30s) │
          │   └──────────────────────────────────────┘
          ▼
[Resolve] WorkflowHandlerRegistry.Get(job.TaskId) → IWorkflowHandler
          │
          ▼
[Execute] handler.ExecuteAsync(context, ct)
          │
          ▼ (handler completes)
[Publish] JobStatusChangedEvent → Completed / CompletedUnsuccessfully / Error
          │
          ▼
[Exit 0 or 1]

--- On cancellation token fired (SIGTERM from WorkflowHost) ---
[handler receives ct cancellation]
[Publish] JobStatusChangedEvent → Cancelled
[Exit 0]

--- On unhandled exception ---
[Publish] JobStatusChangedEvent → Error (exception message)
[Exit 1]
```

---

## Workflow Handler System

### Why Not Reflection + DLL Scanning?

The legacy approach (scan `Workflow.Actions.*` namespace via reflection, load from DLLs) has several problems:
- Convention-based: a typo in the namespace silently breaks discovery
- No compile-time safety — missing handlers only fail at runtime
- Deployment requires dropping compiled DLLs into a folder alongside the engine
- Impossible to unit test without loading real assemblies

### The Handler Registry Pattern

`TaskId` is a string key (e.g. `"send-email"`, `"run-script"`) that maps to a registered `IWorkflowHandler`. The registry is built at startup from DI — explicit, testable, and type-safe.

```csharp
// FlowForge.WorkflowEngine/Handlers/IWorkflowHandler.cs
public interface IWorkflowHandler
{
    string TaskId { get; }
    Task<WorkflowResult> ExecuteAsync(WorkflowContext context, CancellationToken ct);
}
```

```csharp
// FlowForge.WorkflowEngine/Handlers/WorkflowHandlerRegistry.cs
public class WorkflowHandlerRegistry
{
    private readonly IReadOnlyDictionary<string, IWorkflowHandler> _handlers;

    public WorkflowHandlerRegistry(IEnumerable<IWorkflowHandler> handlers)
    {
        _handlers = handlers.ToDictionary(h => h.TaskId, StringComparer.OrdinalIgnoreCase);
    }

    public IWorkflowHandler Get(string taskId)
        => _handlers.TryGetValue(taskId, out var handler)
            ? handler
            : throw new UnknownTaskIdException(taskId);
}
```

### WorkflowContext

Carries the job's input parameters and accumulates outputs across execution:

```csharp
public sealed class WorkflowContext
{
    public Guid   JobId        { get; init; }
    public string TaskId       { get; init; } = default!;
    public string ConnectionId { get; init; } = default!;

    // Input: deserialized from Job.ParametersJson (JSON stored in DB)
    public IReadOnlyDictionary<string, JsonElement> Parameters { get; init; }
        = new Dictionary<string, JsonElement>();

    // Output: written by handler, available after execution
    public Dictionary<string, object> Outputs { get; } = [];

    public T GetParameter<T>(string key)
    {
        if (!Parameters.TryGetValue(key, out var element))
            throw new MissingParameterException(key, TaskId);
        return element.Deserialize<T>()
            ?? throw new MissingParameterException(key, TaskId);
    }
}
```

### WorkflowResult

```csharp
public enum WorkflowResultStatus { Completed, Failed, Cancelled, Error }

public record WorkflowResult(WorkflowResultStatus Status, string? Message = null)
{
    public static WorkflowResult Success()               => new(WorkflowResultStatus.Completed);
    public static WorkflowResult Failure(string reason)  => new(WorkflowResultStatus.Failed, reason);
    public static WorkflowResult Cancellation()          => new(WorkflowResultStatus.Cancelled);
    public static WorkflowResult Fault(string message)   => new(WorkflowResultStatus.Error, message);
}
```

---

## Built-in Handlers

### SendEmailHandler (`"send-email"`)

```csharp
public class SendEmailHandler : IWorkflowHandler
{
    public string TaskId => "send-email";

    public async Task<WorkflowResult> ExecuteAsync(WorkflowContext context, CancellationToken ct)
    {
        var to      = context.GetParameter<string>("to");
        var subject = context.GetParameter<string>("subject");
        var body    = context.GetParameter<string>("body");

        await _emailSender.SendAsync(to, subject, body, ct);
        return WorkflowResult.Success();
    }
}
```

### HttpRequestHandler (`"http-request"`)

```csharp
public class HttpRequestHandler : IWorkflowHandler
{
    public string TaskId => "http-request";

    public async Task<WorkflowResult> ExecuteAsync(WorkflowContext context, CancellationToken ct)
    {
        var url    = context.GetParameter<string>("url");
        var method = context.GetParameter<string>("method");

        var response = await _httpClient.SendAsync(BuildRequest(method, url, context), ct);

        if (!response.IsSuccessStatusCode)
            return WorkflowResult.Failure($"HTTP {(int)response.StatusCode} from {url}");

        context.Outputs["responseBody"] = await response.Content.ReadAsStringAsync(ct);
        return WorkflowResult.Success();
    }
}
```

### RunScriptHandler (`"run-script"`)

The most flexible built-in handler. Executes a Python, JavaScript (Node.js), or shell script in a sandboxed child process. This is the primary extensibility point for users who want custom logic without writing or deploying C# code.

```csharp
public class RunScriptHandler : IWorkflowHandler
{
    public string TaskId => "run-script";

    public async Task<WorkflowResult> ExecuteAsync(WorkflowContext context, CancellationToken ct)
    {
        var interpreter    = context.GetParameter<string>("interpreter");   // "python3", "node", "bash"
        var scriptPath     = context.GetParameter<string>("scriptPath");
        var timeoutMinutes = context.Parameters.TryGetValue("timeoutMinutes", out var t)
                             ? t.GetInt32() : 30;

        // Write input parameters as JSON to a temp file — script reads via INPUT_FILE env var
        var workDir   = Path.Combine(Path.GetTempPath(), "flowforge", context.JobId.ToString("N"));
        Directory.CreateDirectory(workDir);
        var inputFile = Path.Combine(workDir, "input.json");
        await File.WriteAllTextAsync(inputFile, JsonSerializer.Serialize(context.Parameters), ct);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(interpreter, scriptPath)
            {
                WorkingDirectory       = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                Environment            = { ["INPUT_FILE"] = inputFile }
            }
        };

        process.Start();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);

            return ct.IsCancellationRequested
                ? WorkflowResult.Cancellation()
                : WorkflowResult.Failure($"Script timed out after {timeoutMinutes} minutes");
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }

        return process.ExitCode == 0
            ? WorkflowResult.Success()
            : WorkflowResult.Failure(
                $"Script exited with code {process.ExitCode}: "
                + await process.StandardError.ReadToEndAsync(ct));
    }
}
```

---

## Adding Custom Handlers

Two paths — no DLL dropping or reflection required.

### Path 1: Implement `IWorkflowHandler` in C#

Best for complex logic that benefits from .NET tooling, type safety, and unit testing.

```csharp
public class GenerateReportHandler : IWorkflowHandler
{
    public string TaskId => "generate-report";

    public async Task<WorkflowResult> ExecuteAsync(WorkflowContext context, CancellationToken ct)
    {
        // ... custom logic
        return WorkflowResult.Success();
    }
}
```

Register in `Program.cs`:

```csharp
builder.Services.AddScoped<IWorkflowHandler, GenerateReportHandler>();
```

`WorkflowHandlerRegistry` picks it up automatically — no other changes needed.

### Path 2: Use `run-script` with a custom script (zero C# required)

Best for data transformations, Python ML models, shell automations — anything a developer wants to own without touching the engine codebase. Scripts are mounted as a volume in Kubernetes (or baked into the image for tighter control).

```json
// Job.ParametersJson stored in DB
{
  "interpreter": "python3",
  "scriptPath": "/scripts/train_model.py",
  "timeoutMinutes": 120
}
```

```python
# /scripts/train_model.py
import json, os, sys

with open(os.environ["INPUT_FILE"]) as f:
    params = json.load(f)

# ... do actual work

sys.exit(0)   # 0 = success, non-zero = CompletedUnsuccessfully
```

---

## Progress Reporter

Publishes status changes and carries `ConnectionId` so the Web API consumer knows which host-group database to update.

```csharp
public interface IJobReporter
{
    Task ReportStatusAsync(
        Guid jobId, string connectionId, JobStatus status,
        string? message = null, CancellationToken ct = default);
    Task RefreshHeartbeatAsync(Guid jobId, CancellationToken ct = default);
}

public class JobProgressReporter(IMessagePublisher publisher, IRedisService redis) : IJobReporter
{
    public async Task ReportStatusAsync(
        Guid jobId, string connectionId, JobStatus status,
        string? message = null, CancellationToken ct = default)
        => await publisher.PublishAsync(new JobStatusChangedEvent(
            JobId:        jobId,
            ConnectionId: connectionId,
            Status:       status,
            Message:      message,
            UpdatedAt:    DateTimeOffset.UtcNow), ct);

    public async Task RefreshHeartbeatAsync(Guid jobId, CancellationToken ct = default)
        => await redis.RefreshHeartbeatAsync(jobId, ttl: TimeSpan.FromSeconds(30));
}
```

---

## Full Program.cs Flow

```csharp
var jobId        = Guid.Parse(Env("JOB_ID"));
var connectionId = Env("CONNECTION_ID");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

var host     = BuildHost(args);
var registry = host.Services.GetRequiredService<WorkflowHandlerRegistry>();
var reporter = host.Services.GetRequiredService<IJobReporter>();
var jobRepo  = host.Services.GetRequiredKeyedService<IJobRepository>(connectionId);

var job = await jobRepo.GetByIdAsync(jobId, cts.Token)
    ?? throw new JobNotFoundException(jobId);

// Heartbeat loop — runs independently of handler execution
using var heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(5));
_ = Task.Run(async () =>
{
    while (await heartbeatTimer.WaitForNextTickAsync(cts.Token))
        await reporter.RefreshHeartbeatAsync(jobId, cts.Token);
}, cts.Token);

await reporter.ReportStatusAsync(jobId, connectionId, JobStatus.InProgress, ct: cts.Token);

WorkflowResult result;
try
{
    var handler = registry.Get(job.TaskId);
    var context = new WorkflowContext
    {
        JobId        = job.Id,
        TaskId       = job.TaskId,
        ConnectionId = connectionId,
        Parameters   = DeserializeParameters(job.ParametersJson)
    };
    result = await handler.ExecuteAsync(context, cts.Token);
}
catch (OperationCanceledException)
{
    result = WorkflowResult.Cancellation();
}
catch (UnknownTaskIdException ex)
{
    result = WorkflowResult.Fault(ex.Message);
}
catch (Exception ex)
{
    // Unhandled = bug → report as Error, not CompletedUnsuccessfully
    result = WorkflowResult.Fault(ex.Message);
}

// Always send final status — CancellationToken.None so it fires even after cancel
await reporter.ReportStatusAsync(
    jobId, connectionId, ToJobStatus(result), result.Message, CancellationToken.None);

return result.Status is WorkflowResultStatus.Error ? 1 : 0;

static string Env(string name)
    => Environment.GetEnvironmentVariable(name)
       ?? throw new InvalidOperationException($"Required env var '{name}' is not set");
```

---

## Status Mapping

| Scenario | `JobStatus` published |
|---|---|
| Handler returned `Completed` | `Completed` |
| Handler returned `Failed` (business reason) | `CompletedUnsuccessfully` |
| `CancellationToken` cancelled | `Cancelled` |
| Unhandled exception | `Error` |
| `UnknownTaskIdException` | `Error` |

### `Error` vs `CompletedUnsuccessfully`

| Status | Cause | Example |
|---|---|---|
| `Error` | Bug or infrastructure failure — engine did not run correctly | Unhandled exception, unknown `TaskId`, DB unreachable at startup |
| `CompletedUnsuccessfully` | External/business failure — engine ran correctly but the operation failed | SMTP rejected the email, script exited non-zero, HTTP 4xx from target |

---

## Environment Variables

All config is injected by `WorkflowHost` when spawning the child process:

| Variable | Description |
|---|---|
| `JOB_ID` | The job to execute (required) |
| `CONNECTION_ID` | Host group key — selects the correct DB (required) |
| `REDIS_CONNECTION` | Redis connection string |
| `DB_CONNECTION` | Connection string for the host-group Jobs DB |
| `HEARTBEAT_INTERVAL_SECONDS` | Default: `5` |

---

## DI Registration (Program.cs sketch)

```csharp
builder.Services
    .AddInfrastructure(builder.Configuration)
    .AddScoped<IJobReporter, JobProgressReporter>()
    .AddSingleton<WorkflowHandlerRegistry>()
    .AddScoped<IWorkflowHandler, SendEmailHandler>()
    .AddScoped<IWorkflowHandler, HttpRequestHandler>()
    .AddScoped<IWorkflowHandler, RunScriptHandler>();
    // Custom handlers: add here — WorkflowHandlerRegistry picks them up automatically
```