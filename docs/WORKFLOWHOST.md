# WORKFLOWHOST.md — Workflow Host Service

## Responsibility

The Workflow Host is a **Worker Service** deployed as a Kubernetes DaemonSet (one instance per node). Its responsibilities are:

1. Register itself with the system on startup
2. Consume `JobAssignedEvent` from its dedicated host-specific Redis Stream
3. Spawn a `WorkflowEngine` child process per assigned job
4. Monitor child processes and relay cancel requests with a grace period
5. Keep its own host heartbeat alive in Redis

---

## How It Works (Overview)

```
┌────────────────────────────────────────────────────────────┐
│                      WorkflowHost                           │
│                                                            │
│  JobConsumerWorker          CancelConsumerWorker           │
│  ─────────────────          ─────────────────────          │
│  Consume JobAssignedEvent   Consume JobCancelRequestedEvent │
│       ↓                          ↓                         │
│  Spawn WorkflowEngine        Find running process          │
│  child process               Send CancellationToken        │
│       ↓                      Wait grace period             │
│  Track PID + JobId           Force-kill if needed          │
│                                                            │
│  HostHeartbeatWorker                                       │
│  ──────────────────                                        │
│  Refresh host heartbeat key in Redis every 10s             │
└────────────────────────────────────────────────────────────┘
```

---

## Host Identity

Each Workflow Host has a unique `HostId` derived from the Kubernetes node name (set via downward API env var `NODE_NAME`). This means host identity is stable across pod restarts on the same node.

```csharp
// Resolved at startup
var hostId = Environment.GetEnvironmentVariable("NODE_NAME")
    ?? Environment.MachineName;   // local dev fallback
```

### Host Registration on Startup

On startup, the host writes its record to the database and begins refreshing its heartbeat:

```csharp
// In Program.cs or a startup task
await hostRegistry.RegisterAsync(new WorkflowHostRegistration(
    HostId:      hostId,
    HostGroupId: options.HostGroupId,  // from config
    RegisteredAt: DateTimeOffset.UtcNow
));
```

---

## Job Consumer Worker

Subscribes to the host-specific Redis Stream `flowforge:host:{hostId}`.

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    await foreach (var @event in _consumer.ConsumeAsync<JobAssignedEvent>(
        streamName:    StreamNames.HostStream(_hostId),
        consumerGroup: "workflow-host",
        consumerName:  _hostId,
        ct:            stoppingToken))
    {
        // Fire-and-forget with tracking; don't await here to allow parallel jobs
        _ = SpawnEngineAsync(@event, stoppingToken);
    }
}

private async Task SpawnEngineAsync(JobAssignedEvent @event, CancellationToken hostStopping)
{
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(hostStopping);
    _activeJobs[event.JobId] = cts;   // register for cancel lookup

    try
    {
        await _processManager.RunAsync(
            jobId:  @event.JobId,
            ct:     cts.Token);
    }
    finally
    {
        _activeJobs.TryRemove(@event.JobId, out _);
    }
}
```

> A `ConcurrentDictionary<Guid, CancellationTokenSource> _activeJobs` tracks running engines so cancel requests can reach them.

---

## Process Manager

### Interface

```csharp
public interface IProcessManager
{
    /// <summary>
    /// Runs the WorkflowEngine for the given job.
    /// Returns when the engine process exits.
    /// Cancellation triggers graceful stop → force kill after grace period.
    /// </summary>
    Task RunAsync(Guid jobId, CancellationToken ct);
}
```

### DockerProcessManager (primary — K8s native)

In a Kubernetes environment, `WorkflowEngine` runs as an ephemeral container (or via `docker run` if Docker-in-Docker is configured). Cancellation sends `SIGTERM` first, then `SIGKILL` after the grace period.

```csharp
public class DockerProcessManager : IProcessManager
{
    private readonly DockerProcessOptions _options;

    public async Task RunAsync(Guid jobId, CancellationToken ct)
    {
        var containerName = $"engine-{jobId:N}";

        // Start container
        await RunCommandAsync("docker", [
            "run", "--rm",
            "--name", containerName,
            "-e", $"JOB_ID={jobId}",
            "-e", $"REDIS_CONNECTION={_options.RedisConnection}",
            _options.EngineImage
        ]);

        // Wait for exit or cancellation
        try
        {
            await WaitForContainerAsync(containerName, ct);
        }
        catch (OperationCanceledException)
        {
            // Graceful: send SIGTERM, wait grace period, then SIGKILL
            await RunCommandAsync("docker", ["stop", "-t",
                _options.GracePeriodSeconds.ToString(), containerName]);
        }
    }
}
```

### NativeProcessManager (fallback — Linux process group)

Used in non-Docker or local dev environments.

```csharp
public class NativeProcessManager : IProcessManager
{
    public async Task RunAsync(Guid jobId, CancellationToken ct)
    {
        var enginePath = _options.EnginePath;  // path to WorkflowEngine binary

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(enginePath)
            {
                Arguments = $"--job-id {jobId}",
                UseShellExecute = false,

                // Group the engine + any children it spawns
                // On Linux: new session = new process group
            }
        };

        process.Start();

        ct.Register(() =>
        {
            // SIGTERM to entire process group
            KillProcessGroup(process.Id, signal: Sigterm);

            // Allow grace period, then SIGKILL
            _ = Task.Delay(_options.GracePeriodMs)
                    .ContinueWith(_ => KillProcessGroup(process.Id, signal: Sigkill));
        });

        await process.WaitForExitAsync(ct);
    }

    private static void KillProcessGroup(int pid, int signal)
        => Syscall.Kill(-pid, signal);   // negative PID targets whole group
}
```

### Registration (choose via config)

```csharp
var processManagerType = builder.Configuration["WorkflowHost:ProcessManager"];

if (processManagerType == "Docker")
    builder.Services.AddSingleton<IProcessManager, DockerProcessManager>();
else
    builder.Services.AddSingleton<IProcessManager, NativeProcessManager>();
```

---

## Cancel Consumer Worker

Subscribes to `flowforge:job-cancel-requested` and cancels the matching running engine.

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    await foreach (var @event in _consumer.ConsumeAsync<JobCancelRequestedEvent>(
        StreamNames.JobCancelRequested, "workflow-host", _hostId, stoppingToken))
    {
        if (_activeJobs.TryGetValue(@event.JobId, out var cts))
        {
            _logger.LogInformation("Cancelling job {JobId}", @event.JobId);
            await cts.CancelAsync();
        }
        else
        {
            // Job is not running on this host — ignore
            _logger.LogDebug(
                "Cancel requested for job {JobId} which is not running on this host",
                @event.JobId);
        }
    }
}
```

The cancellation token reaching `IProcessManager.RunAsync` is what triggers the graceful stop. The `WorkflowEngine` itself handles the `Cancelled` status update via its own reporting mechanism.

---

## Host Heartbeat Worker

Keeps the host's heartbeat key alive in Redis. If this key expires, `JobOrchestrator` will stop routing new jobs to this host.

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    while (!stoppingToken.IsCancellationRequested)
    {
        await _redis.SetAsync(
            key:    $"host:heartbeat:{_hostId}",
            value:  DateTimeOffset.UtcNow.ToString("O"),
            expiry: TimeSpan.FromSeconds(30));

        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
    }

    // On graceful shutdown — mark host offline
    await _hostRegistry.MarkOfflineAsync(_hostId);
}
```

---

## Kubernetes: DaemonSet Notes

Because WorkflowHost runs as a DaemonSet, exactly one pod runs per node.

```yaml
# k8s/workflow-host/daemonset.yaml (sketch)
spec:
  template:
    spec:
      containers:
        - name: workflow-host
          env:
            - name: NODE_NAME
              valueFrom:
                fieldRef:
                  fieldPath: spec.nodeName
            - name: HOST_GROUP_ID
              valueFrom:
                configMapKeyRef:
                  name: app-config
                  key: defaultHostGroupId
```

`NODE_NAME` is injected by Kubernetes downward API — the host uses this as its stable identity.

---

## Configuration

```json
{
  "WorkflowHost": {
    "HostGroupId": "00000000-0000-0000-0000-000000000001",
    "ProcessManager": "Docker",
    "GracePeriodSeconds": 30,
    "MaxConcurrentJobs": 5
  },
  "Docker": {
    "EngineImage": "flowforge/workflow-engine:latest",
    "GracePeriodSeconds": 30
  },
  "Redis": {
    "ConnectionString": "redis:6379"
  }
}
```

---

## DI Registration (Program.cs sketch)

```csharp
var hostId = Environment.GetEnvironmentVariable("NODE_NAME") ?? Environment.MachineName;
builder.Services.AddSingleton(_ => new HostIdentity(hostId));

builder.Services
    .AddInfrastructure(builder.Configuration)
    .AddSingleton<IProcessManager, DockerProcessManager>()  // or NativeProcessManager
    .AddHostedService<JobConsumerWorker>()
    .AddHostedService<CancelConsumerWorker>()
    .AddHostedService<HostHeartbeatWorker>();
```