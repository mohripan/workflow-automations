# JOBORCHESTRATOR.md — Job Orchestrator Service

## Responsibility

The Job Orchestrator is a **Worker Service** that:

1. Consumes `JobCreatedEvent` from Redis Streams
2. Selects the best available host within the job's `HostGroup` using round-robin load balancing
3. Publishes a `JobAssignedEvent` to the target host's dedicated stream
4. Monitors job heartbeats and marks dead jobs as `Error`

---

## How It Works (Overview)

```
┌───────────────────────────────────────────────────────────────┐
│                        JobOrchestrator                         │
│                                                               │
│   JobDispatcherWorker          HeartbeatMonitorWorker         │
│   ─────────────────────        ──────────────────────         │
│   Consume JobCreatedEvent      Scan Redis for expired         │
│       ↓                        heartbeat keys                 │
│   Find available hosts         Mark affected jobs → Error     │
│   in HostGroup                                                │
│       ↓                                                       │
│   Round-robin select host                                     │
│       ↓                                                       │
│   Update Job: Started + HostId                               │
│       ↓                                                       │
│   Publish JobAssignedEvent                                    │
│   to host-specific stream                                     │
└───────────────────────────────────────────────────────────────┘
```

---

## Job Dispatcher Worker

Consumes `flowforge:job-created` stream and dispatches each job to an appropriate host.

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    await foreach (var @event in _consumer.ConsumeAsync<JobCreatedEvent>(
        StreamNames.JobCreated,
        consumerGroup: "job-orchestrator",
        consumerName: _options.InstanceName,
        ct: stoppingToken))
    {
        await DispatchJobAsync(@event, stoppingToken);
    }
}

private async Task DispatchJobAsync(JobCreatedEvent @event, CancellationToken ct)
{
    var job = await _jobRepo.GetByIdAsync(@event.JobId, ct)
        ?? throw new JobNotFoundException(@event.JobId);

    // Get online hosts in the target group
    var hosts = await _hostRepo.GetOnlineByGroupAsync(@event.HostGroupId, ct);
    if (hosts.Count == 0)
    {
        _logger.LogWarning(
            "No available hosts in group {HostGroupId} for job {JobId}",
            @event.HostGroupId, @event.JobId);
        // Leave job as Pending — will be retried on next available host registration
        return;
    }

    // Select host via round-robin
    var selectedHost = _loadBalancer.Select(hosts, @event.HostGroupId);

    // Transition job status and assign host
    job.Transition(JobStatus.Started);
    job.AssignHost(selectedHost.Id);
    await _jobRepo.SaveAsync(job, ct);

    // Publish to host-specific stream
    await _publisher.PublishAsync(new JobAssignedEvent(
        JobId:    job.Id,
        HostId:   selectedHost.Id,
        AssignedAt: DateTimeOffset.UtcNow
    ), targetStream: StreamNames.HostStream(selectedHost.Id), ct);

    _logger.LogInformation(
        "Job {JobId} assigned to host {HostId}", job.Id, selectedHost.Id);
}
```

---

## Load Balancer

### Interface

```csharp
public interface ILoadBalancer
{
    WorkflowHost Select(IReadOnlyList<WorkflowHost> hosts, Guid hostGroupId);
}
```

### Round-Robin Implementation

Tracks the last-selected index per `HostGroup` in memory. Thread-safe via `ConcurrentDictionary`.

```csharp
public class RoundRobinLoadBalancer : ILoadBalancer
{
    private readonly ConcurrentDictionary<Guid, int> _counters = new();

    public WorkflowHost Select(IReadOnlyList<WorkflowHost> hosts, Guid hostGroupId)
    {
        if (hosts.Count == 0)
            throw new NoAvailableHostException(hostGroupId);

        var index = _counters.AddOrUpdate(
            key:            hostGroupId,
            addValue:       0,
            updateValueFactory: (_, prev) => (prev + 1) % hosts.Count);

        return hosts[index];
    }
}
```

> **Note:** Since `JobOrchestrator` runs as a single replica (see SPECS.md), in-memory counters are sufficient. For multi-replica HA setups, migrate the counter to a Redis `INCR` key.

---

## Heartbeat Monitor Worker

Runs on a fixed interval and scans for jobs whose heartbeat key has expired in Redis.

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    while (!stoppingToken.IsCancellationRequested)
    {
        await Task.Delay(_options.HeartbeatScanIntervalMs, stoppingToken);
        await ScanExpiredHeartbeatsAsync(stoppingToken);
    }
}

private async Task ScanExpiredHeartbeatsAsync(CancellationToken ct)
{
    // Get all jobs currently in InProgress status
    var activeJobs = await _jobRepo.GetByStatusAsync(JobStatus.InProgress, ct);

    foreach (var job in activeJobs)
    {
        var isAlive = await _redis.IsHeartbeatAliveAsync(job.Id);
        if (isAlive) continue;

        _logger.LogWarning(
            "Heartbeat expired for job {JobId}, marking as Error", job.Id);

        job.Transition(JobStatus.Error);
        job.SetError("Heartbeat expired — engine process may have crashed");
        await _jobRepo.SaveAsync(job, ct);

        // Notify frontend via the status-changed event
        await _publisher.PublishAsync(new JobStatusChangedEvent(
            JobId:     job.Id,
            Status:    JobStatus.Error,
            UpdatedAt: DateTimeOffset.UtcNow
        ), ct);
    }
}
```

### Heartbeat Key Convention

| Redis Key | TTL | Set By |
|---|---|---|
| `heartbeat:{jobId}` | 30 seconds | `WorkflowEngine` (every 5s) |

If the key does not exist when `IsHeartbeatAliveAsync` checks, the heartbeat is considered expired.

---

## Host Registration

Workflow Hosts register themselves in the database when they start and deregister (or become stale) when they stop. The orchestrator only dispatches to hosts with status `Online`.

### Host Online/Offline Detection
- `WorkflowHost` writes a heartbeat key `host:heartbeat:{hostId}` to Redis with TTL 30s on startup and refreshes every 10s.
- `JobOrchestrator` calls `GetOnlineByGroupAsync` which queries hosts whose `LastHeartbeat` is within the last 30s.
- Alternatively: listen to host heartbeat events via a dedicated stream.

---

## Handling Pending Jobs With No Available Host

When `DispatchJobAsync` finds no online hosts, the job stays `Pending`. To handle this without tight-loop retry:

- `JobDispatcherWorker` subscribes to a secondary stream `flowforge:host-registered` (published by WorkflowHost on startup).
- On receiving a host registration event, it re-scans for `Pending` jobs in that host's group.

```csharp
// On host-registered event
var pendingJobs = await _jobRepo.GetPendingByGroupAsync(@event.HostGroupId, ct);
foreach (var job in pendingJobs)
    await DispatchJobAsync(new JobCreatedEvent(job.Id, job.AutomationId, job.HostGroupId), ct);
```

---

## Cancel Flow

The orchestrator is **not** directly involved in cancelling a running job — the Web API publishes `JobCancelRequestedEvent` directly to the Workflow Host. However, the orchestrator handles removing jobs that are still `Pending`:

```csharp
// Called when Web API publishes a cancel request for a Pending job
private async Task HandleCancelPendingJobAsync(Guid jobId, CancellationToken ct)
{
    var job = await _jobRepo.GetByIdAsync(jobId, ct)!;
    if (job.Status != JobStatus.Pending) return;   // already dispatched, not our concern

    job.Transition(JobStatus.Removed);
    await _jobRepo.SaveAsync(job, ct);
}
```

---

## Configuration

```json
{
  "JobOrchestrator": {
    "InstanceName": "orchestrator-1",
    "HeartbeatScanIntervalMs": 15000
  },
  "Redis": {
    "ConnectionString": "redis:6379",
    "HeartbeatTtlSeconds": 30
  },
  "ConnectionStrings": {
    "DefaultConnection": "Host=postgres;Database=flowforge;..."
  }
}
```

---

## DI Registration (Program.cs sketch)

```csharp
builder.Services
    .AddInfrastructure(builder.Configuration)
    .AddSingleton<ILoadBalancer, RoundRobinLoadBalancer>()
    .AddHostedService<JobDispatcherWorker>()
    .AddHostedService<HeartbeatMonitorWorker>();
```