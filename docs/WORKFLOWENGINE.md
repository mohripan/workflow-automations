# WORKFLOWENGINE.md — Workflow Engine Service

## Responsibility

The Workflow Engine is a **short-lived Console Application** (not a long-running service). It is spawned by `WorkflowHost` as a child process to execute exactly **one Job**, then exits.

It is responsible for:
1. Loading the job definition from the database on startup
2. Executing the job's activities in order
3. Reporting progress and status changes to Redis Streams
4. Writing a heartbeat key to Redis every 5 seconds while running
5. Responding to cancellation gracefully (via `CancellationToken`)

---

## Lifecycle

```
WorkflowHost spawns WorkflowEngine
          │
          ▼
[Startup] Read JOB_ID from env var
          │
          ▼
[DB] Load Job + activity definitions
          │
          ▼
[Publish] JobStatusChangedEvent (InProgress)
          │
          ▼
[Loop]  Execute activities sequentially
        │  Every 5s → refresh heartbeat key in Redis
        │  On each activity complete → report progress
        │
        ▼ (all activities done)
[Publish] JobStatusChangedEvent (Completed)
          │
          ▼
[Exit 0]

--- If cancellation token fires ---
[Publish] JobStatusChangedEvent (Cancelled)
[Exit 0]

--- If unhandled exception ---
[Publish] JobStatusChangedEvent (Error, message=exception)
[Exit 1]

--- If activity logic fails non-fatally ---
[Publish] JobStatusChangedEvent (CompletedUnsuccessfully, reason=...)
[Exit 0]
```

---

## Startup: Job ID from Environment

The engine receives the `JobId` as an environment variable injected by `WorkflowHost`:

```csharp
// Program.cs
var jobId = Guid.Parse(
    Environment.GetEnvironmentVariable("JOB_ID")
    ?? throw new InvalidOperationException("JOB_ID env var is required"));
```

All other configuration (Redis connection, DB connection) also comes from environment variables, which `WorkflowHost` sets when spawning the process.

---

## Activities

### Interface

```csharp
public interface IActivity
{
    string ActivityType { get; }

    Task<ActivityResult> ExecuteAsync(ActivityContext context, CancellationToken ct);
}
```

### ActivityContext

Carries the job's input parameters and collects outputs:

```csharp
public record ActivityContext(
    Guid                             JobId,
    IReadOnlyDictionary<string, object> InputParameters,
    IActivityOutputStore             Outputs         // write output for downstream activities
);
```

### ActivityResult

```csharp
public enum ActivityResultStatus { Success, Failed, Cancelled }

public record ActivityResult(
    ActivityResultStatus Status,
    string?              ErrorMessage = null
);
```

### Built-in Activities

| Class | `ActivityType` | Description |
|---|---|---|
| `SendEmailActivity` | `send-email` | Send email via SMTP or SendGrid |
| `RunScriptActivity` | `run-script` | Execute a Python or shell script in a sandboxed process |
| `HttpRequestActivity` | `http-request` | Make an HTTP call (GET/POST/etc.) |

### Adding New Activities

Implement `IActivity` and register it:

```csharp
builder.Services.AddScoped<IActivity, MyCustomActivity>();
```

The `ActivityRunner` resolves activities by matching `ActivityType` string to the registered implementations.

---

## Activity Runner

Executes activities in sequence. Stops early on failure or cancellation.

```csharp
public class ActivityRunner
{
    public async Task<JobFinalStatus> RunAsync(
        Job job,
        IReadOnlyList<ActivityDefinition> activities,
        CancellationToken ct)
    {
        var outputStore = new ActivityOutputStore();

        foreach (var definition in activities)
        {
            ct.ThrowIfCancellationRequested();

            var activity = _activities.SingleOrDefault(a => a.ActivityType == definition.Type)
                ?? throw new UnknownActivityException(definition.Type);

            var context = new ActivityContext(
                JobId:           job.Id,
                InputParameters: definition.Parameters,
                Outputs:         outputStore);

            var result = await activity.ExecuteAsync(context, ct);

            switch (result.Status)
            {
                case ActivityResultStatus.Success:
                    _reporter.ReportProgress(definition.Name);
                    break;

                case ActivityResultStatus.Failed:
                    return JobFinalStatus.CompletedUnsuccessfully(result.ErrorMessage!);

                case ActivityResultStatus.Cancelled:
                    return JobFinalStatus.Cancelled;
            }
        }

        return JobFinalStatus.Completed;
    }
}
```

---

## Progress Reporter

Publishes status changes to `flowforge:job-status-changed` and refreshes the heartbeat key.

### Interface

```csharp
public interface IJobReporter
{
    Task ReportStatusAsync(Guid jobId, JobStatus status, string? message = null, CancellationToken ct = default);
    Task RefreshHeartbeatAsync(Guid jobId, CancellationToken ct = default);
}
```

### Implementation

```csharp
public class JobProgressReporter : IJobReporter
{
    public async Task ReportStatusAsync(
        Guid jobId, JobStatus status, string? message = null, CancellationToken ct = default)
    {
        await _publisher.PublishAsync(new JobStatusChangedEvent(
            JobId:     jobId,
            Status:    status,
            Message:   message,
            UpdatedAt: DateTimeOffset.UtcNow
        ), ct);
    }

    public async Task RefreshHeartbeatAsync(Guid jobId, CancellationToken ct = default)
    {
        await _redis.RefreshHeartbeatAsync(jobId, ttl: TimeSpan.FromSeconds(30));
    }
}
```

### Heartbeat Timer

A background `Timer` refreshes the heartbeat independent of activity execution:

```csharp
using var heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(5));
_ = Task.Run(async () =>
{
    while (await heartbeatTimer.WaitForNextTickAsync(ct))
        await _reporter.RefreshHeartbeatAsync(jobId, ct);
}, ct);
```

---

## Full Program.cs Flow

```csharp
// Program.cs
var jobId = Guid.Parse(Environment.GetEnvironmentVariable("JOB_ID")!);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();   // SIGTERM → graceful cancel
};

// Build host / DI
var host = BuildHost(args);
var runner   = host.Services.GetRequiredService<ActivityRunner>();
var reporter = host.Services.GetRequiredService<IJobReporter>();
var jobRepo  = host.Services.GetRequiredService<IJobRepository>();

// Load job
var job = await jobRepo.GetByIdAsync(jobId, cts.Token)
    ?? throw new JobNotFoundException(jobId);

// Start heartbeat
using var heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(5));
var heartbeatTask = Task.Run(async () =>
{
    while (await heartbeatTimer.WaitForNextTickAsync(cts.Token))
        await reporter.RefreshHeartbeatAsync(jobId, cts.Token);
});

// Report InProgress
await reporter.ReportStatusAsync(jobId, JobStatus.InProgress, ct: cts.Token);

// Execute
JobFinalStatus finalStatus;
try
{
    finalStatus = await runner.RunAsync(job, job.Activities, cts.Token);
}
catch (OperationCanceledException)
{
    finalStatus = JobFinalStatus.Cancelled;
}
catch (Exception ex)
{
    finalStatus = JobFinalStatus.Error(ex.Message);
}

// Report final status
await reporter.ReportStatusAsync(
    jobId,
    finalStatus.ToJobStatus(),
    finalStatus.Message,
    CancellationToken.None);   // use None — we want this to send even after cancel

return finalStatus.IsError ? 1 : 0;
```

---

## Status Mapping

| Scenario | `JobStatus` published |
|---|---|
| All activities succeeded | `Completed` |
| Activity returned `Failed` | `CompletedUnsuccessfully` |
| `CancellationToken` cancelled | `Cancelled` |
| Unhandled exception | `Error` |

### Difference: `Error` vs `CompletedUnsuccessfully`

| Status | Cause |
|---|---|
| `Error` | Bug in the engine or infrastructure failure (exception propagated unexpectedly) |
| `CompletedUnsuccessfully` | External/business failure (e.g. SMTP server rejected email, script exited non-zero). The engine ran correctly — the job's target operation failed. |

---

## RunScript Activity (Security Notes)

`RunScriptActivity` executes user-provided scripts. This must be sandboxed:

- Scripts run in a separate child process with restricted permissions.
- Working directory is an isolated temp folder, cleaned up after execution.
- Execution time is limited via `CancellationToken` + timeout.
- No network access by default (enforced via `seccomp` in Docker/K8s).

```csharp
public class RunScriptActivity : IActivity
{
    public string ActivityType => "run-script";

    public async Task<ActivityResult> ExecuteAsync(ActivityContext context, CancellationToken ct)
    {
        var config = context.GetParameter<RunScriptConfig>();

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(config.Interpreter, config.ScriptPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                WorkingDirectory       = CreateIsolatedWorkDir(context.JobId)
            }
        };

        process.Start();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(config.TimeoutMinutes));

        await process.WaitForExitAsync(timeout.Token);

        return process.ExitCode == 0
            ? ActivityResult.Success()
            : ActivityResult.Failed($"Script exited with code {process.ExitCode}");
    }
}
```

---

## Configuration (via environment variables)

The engine reads all config from env vars (injected by `WorkflowHost`):

| Env Var | Description |
|---|---|
| `JOB_ID` | The job to execute (required) |
| `REDIS_CONNECTION` | Redis connection string |
| `DB_CONNECTION` | PostgreSQL connection string |
| `ENGINE_HEARTBEAT_INTERVAL_SECONDS` | Default: 5 |

---

## DI Registration (Program.cs sketch)

```csharp
builder.Services
    .AddInfrastructure(builder.Configuration)
    .AddScoped<IJobReporter, JobProgressReporter>()
    .AddScoped<ActivityRunner>()
    .AddScoped<IActivity, SendEmailActivity>()
    .AddScoped<IActivity, RunScriptActivity>()
    .AddScoped<IActivity, HttpRequestActivity>();
```