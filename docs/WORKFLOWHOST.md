# WORKFLOWHOST.md — Workflow Host Service

## Responsibility

The Workflow Host is a worker service deployed as a Kubernetes DaemonSet (one instance per node). Its responsibilities are:
1. Register itself in the platform database on startup
2. Consume `JobAssignedEvent` from its host-specific Redis Stream and spawn a `WorkflowEngine` process for each job
3. Track active processes for cancellation
4. Relay `JobCancelRequestedEvent` to the correct running process
5. Maintain a Redis heartbeat key so `HeartbeatMonitorWorker` knows this host is alive

---

## Host Identity

The host ID is derived from:
```csharp
Environment.GetEnvironmentVariable("NODE_NAME") ?? Environment.MachineName
```

In Kubernetes, `NODE_NAME` is injected via the Downward API:
```yaml
env:
  - name: NODE_NAME
    valueFrom:
      fieldRef:
        fieldPath: spec.nodeName
```

The host subscribes to the stream `flowforge:host:{hostId}`. This stream is created/bootstrapped on startup.

---

## JobConsumerWorker

Consumes `JobAssignedEvent` from `flowforge:host:{hostId}` (consumer group `"workflow-host"`, consumer name = `hostId`).

For each event, spawns a fire-and-forget `RunJobAsync` task. The consumer loop itself never awaits job completion — it immediately moves on to the next message.

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    await foreach (var @event in consumer.ConsumeAsync<JobAssignedEvent>(
        StreamNames.HostStream(_hostId), "workflow-host", _hostId, stoppingToken))
    {
        _ = RunJobAsync(@event, stoppingToken);   // fire-and-forget
    }
}
```

`RunJobAsync` tracks its `CancellationTokenSource` in `_activeJobs` for cancel support, then calls `IProcessManager.RunAsync(jobId, automationId, connectionId, ct)`.

On exception → DLQ write + remove from `_activeJobs`.

### Cancellation
```csharp
public bool TryCancel(Guid jobId)
{
    if (_activeJobs.TryGetValue(jobId, out var cts))
    {
        cts.Cancel();
        return true;
    }
    return false;
}
```
Called by `CancelConsumerWorker`.

---

## IProcessManager

```csharp
public interface IProcessManager
{
    Task RunAsync(Guid jobId, Guid automationId, string connectionId, CancellationToken ct);
}
```

### NativeProcessManager
Spawns `FlowForge.WorkflowEngine` as a child process. Detects whether `WorkflowHost:EnginePath` ends in `.dll` and launches via `dotnet <dll>` accordingly (required in the Docker container where the engine is published as a managed assembly).

Environment variables set on the child:
- `JOB_ID` = `jobId.ToString()`
- `JOB_AUTOMATION_ID` = `automationId.ToString()`
- `CONNECTION_ID` = `connectionId`
- All environment variables from the parent process (includes connection strings, Redis config, SMTP credentials, etc.)

Awaits process exit. If the `CancellationToken` fires, sends `SIGTERM` to the process and awaits graceful exit.

### DockerProcessManager
Runs `WorkflowEngine` inside a Docker container via `docker run`. Used when WorkflowHost itself runs outside a container but jobs need container isolation.

---

## CancelConsumerWorker

Consumes `JobCancelRequestedEvent` from `flowforge:job-cancel-requested` (consumer group `"workflow-host"`).

`JobConsumerWorker` is registered as an explicit singleton (`AddSingleton<JobConsumerWorker>()`) so `CancelConsumerWorker` can inject it directly without a circular dependency via `IEnumerable<IHostedService>`.

```csharp
if (@event.HostId.ToString() == _hostId || @event.HostId == Guid.Empty)
    jobConsumer.TryCancel(@event.JobId);
```

`HostId == Guid.Empty` means broadcast cancel (cancel on whichever host holds the job). If `TryCancel` returns `false` the event is silently acknowledged — the job is not on this host.

---

## HostHeartbeatWorker

Publishes `host:heartbeat:{hostId}` to Redis every `HostHeartbeatOptions.PublishIntervalSeconds` seconds (default: 10). The key TTL is 30 seconds — if the host stops publishing, `HeartbeatMonitorWorker` will detect the expiry after at most 30 seconds and mark the host offline.

```json
// appsettings.json
"HostHeartbeat": {
  "PublishIntervalSeconds": 10
}
```

---

## Health Checks

```
GET /health/live   → 200 always (liveness)
GET /health/ready  → checks PostgreSQL (job DB) + Redis
```

---

## Configuration

```json
{
  "HostHeartbeat": {
    "PublishIntervalSeconds": 10
  },
  "WorkflowHost": {
    "EnginePath": "/app/engine/FlowForge.WorkflowEngine.dll"
  },
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=flowforge_platform;Username=postgres;Password=postgres"
  },
  "JobConnections": {
    "wf-jobs-minion": {
      "ConnectionString": "Host=localhost;Port=5433;Database=flowforge_minion;Username=postgres;Password=postgres",
      "Provider": "PostgreSQL"
    }
  },
  "Redis": { "ConnectionString": "localhost:6379" },
  "OpenTelemetry": { "OtlpEndpoint": "http://localhost:4317" }
}
```

`WorkflowHost:EnginePath` points to the engine binary. When the path ends in `.dll`, `NativeProcessManager` launches it via `dotnet <dll>` (Linux container). When running locally on Windows, it can point to a `.exe` or an absolute `.dll` path.

---

## DI Registration (Program.cs)

```csharp
builder.Services.AddInfrastructure(builder.Configuration, "WorkflowHost");
builder.Services.AddSingleton<IProcessManager, NativeProcessManager>();

builder.Services.Configure<HostHeartbeatOptions>(
    builder.Configuration.GetSection(HostHeartbeatOptions.SectionName));

// JobConsumerWorker registered as singleton so CancelConsumerWorker can inject it directly
builder.Services.AddSingleton<JobConsumerWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobConsumerWorker>());
builder.Services.AddHostedService<CancelConsumerWorker>();
builder.Services.AddHostedService<HostHeartbeatWorker>();
```
