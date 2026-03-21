# JOBORCHESTRATOR.md — Job Orchestrator Service

## Responsibility

The Job Orchestrator is a worker service that:
1. Consumes `JobCreatedEvent` from Redis and dispatches each job to an available host
2. Monitors host heartbeats and marks unresponsive hosts offline
3. Periodically re-queues jobs that are stuck in `Pending` status so they are dispatched once hosts come back online

It has **read/write access to the job databases** (to update `Job.Status` and `Job.HostId`) and to the platform database (to read `WorkflowHost` records).

---

## Architecture Overview

```
flowforge:job-created  ──►  JobDispatcherWorker
                                  │
                          IWorkflowHostRepository (platform DB)
                          IJobRepository keyed by ConnectionId (job DB)
                          ILoadBalancer (round-robin)
                                  │
                          publisher ──► flowforge:host:{selectedHostName}
                                  │
                      (on error) DlqWriter ──► flowforge:dlq


Platform DB (WorkflowHosts table)
         │
         ▼
HeartbeatMonitorWorker (every N seconds)
         │
    IRedisService → host:heartbeat:{hostName}   (set by WorkflowHost)


Job DBs (all configured connections)
         │
         ▼
PendingJobScannerWorker (every N seconds)
         │
    re-publishes JobCreatedEvent for stale Pending jobs
```

---

## JobDispatcherWorker

Consumes `JobCreatedEvent` from `flowforge:job-created` (consumer group `"job-orchestrator"`, consumer name `"orchestrator-1"`).

For each event:
1. Load the `Job` from the job DB keyed by `event.ConnectionId`
2. Load online hosts for `job.HostGroupId`
3. If **no online hosts** → log warning and `continue` (job stays `Pending`; `PendingJobScannerWorker` will re-queue it)
4. Select a host via `ILoadBalancer.Select(...)`
5. Transition job to `Started`, assign `HostId`, call `jobRepo.SaveAsync`
6. Publish `JobAssignedEvent` to `flowforge:host:{selectedHost.Name}`

On exception → `IDlqWriter.WriteAsync` + continue (never crashes the consumer).

Uses OpenTelemetry span: `"dispatch job {jobId}"`.

> **Note:** the routing key uses `selectedHost.Name` (matching the `NODE_NAME` env var on the WorkflowHost container), not the host's database GUID.

---

## ILoadBalancer

```csharp
public interface ILoadBalancer
{
    WorkflowHost Select(IReadOnlyList<WorkflowHost> hosts, Guid hostGroupId);
}
```

### RoundRobinLoadBalancer
Maintains a `ConcurrentDictionary<Guid, int>` counter per host group. Each call returns `hosts[counter % hosts.Count]` and atomically increments the counter.

---

## HeartbeatMonitorWorker

Runs every `HeartbeatMonitorOptions.CheckIntervalSeconds` seconds (default: 15).

For every `WorkflowHost` in the platform DB:
- Checks Redis key `host:heartbeat:{host.Name}` (set by `HostHeartbeatWorker` in WorkflowHost with a 30-second TTL)
- Key **absent** + host is `IsOnline=true` → `host.MarkOffline()` + save
- Key **present** + host is `IsOnline=false` → `host.MarkOnline()` + save

The key uses `host.Name` (the human-readable node name), which matches the `NODE_NAME` env var set on each WorkflowHost container.

```json
// appsettings.json
"HeartbeatMonitor": {
  "CheckIntervalSeconds": 15
}
```

---

## PendingJobScannerWorker

Runs every `PendingJobScannerOptions.ScanIntervalSeconds` seconds (default: 30). After waiting, it:

1. Reads all `JobConnections` keys from configuration
2. For each connection, loads all `Pending` jobs older than `StaleAfterSeconds` (default: 15)
3. Re-publishes `JobCreatedEvent` to `flowforge:job-created` for each stale job

This allows jobs that were not dispatched (because no hosts were online) to be re-attempted once hosts recover. If a job has already been dispatched by the time the scanner runs, it will have transitioned out of `Pending` and will not be re-queued.

```json
// appsettings.json
"PendingJobScanner": {
  "ScanIntervalSeconds": 30,
  "StaleAfterSeconds": 15
}
```

---

## Cancel Flow

The cancel flow does not pass through JobOrchestrator directly. `WebApi` publishes `JobCancelRequestedEvent` to `flowforge:job-cancel-requested`. `WorkflowHost`'s `CancelConsumerWorker` handles it.

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
  "HeartbeatMonitor": {
    "CheckIntervalSeconds": 15
  },
  "PendingJobScanner": {
    "ScanIntervalSeconds": 30,
    "StaleAfterSeconds": 15
  },
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=flowforge_platform;Username=postgres;Password=postgres"
  },
  "JobConnections": {
    "wf-jobs-minion": {
      "ConnectionString": "Host=localhost;Port=5433;Database=flowforge_minion;Username=postgres;Password=postgres",
      "Provider": "PostgreSQL"
    },
    "wf-jobs-titan": {
      "ConnectionString": "Host=localhost;Port=5434;Database=flowforge_titan;Username=postgres;Password=postgres",
      "Provider": "PostgreSQL"
    }
  },
  "Redis": { "ConnectionString": "localhost:6379" },
  "OpenTelemetry": { "OtlpEndpoint": "http://localhost:4317" }
}
```

---

## DI Registration (Program.cs)

```csharp
builder.Services.AddInfrastructure(builder.Configuration, "JobOrchestrator");
builder.Services.AddSingleton<ILoadBalancer, RoundRobinLoadBalancer>();

builder.Services.Configure<HeartbeatMonitorOptions>(
    builder.Configuration.GetSection(HeartbeatMonitorOptions.SectionName));
builder.Services.Configure<PendingJobScannerOptions>(
    builder.Configuration.GetSection(PendingJobScannerOptions.SectionName));

builder.Services.AddHostedService<JobDispatcherWorker>();
builder.Services.AddHostedService<HeartbeatMonitorWorker>();
builder.Services.AddHostedService<PendingJobScannerWorker>();
```
