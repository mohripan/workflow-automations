# JOBORCHESTRATOR.md — Job Orchestrator Service

## Responsibility

The Job Orchestrator is a worker service that:
1. Consumes `JobCreatedEvent` from Redis and dispatches each job to an available host
2. Monitors host heartbeats and marks unresponsive hosts offline

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
                          publisher ──► flowforge:host:{selectedHostId}
                                  │
                      (on error) DlqWriter ──► flowforge:dlq


Platform DB (WorkflowHosts table)
         │
         ▼
HeartbeatMonitorWorker (every N seconds)
         │
    IRedisService → host:heartbeat:{hostId}   (set by WorkflowHost)
```

---

## JobDispatcherWorker

Consumes `JobCreatedEvent` from `flowforge:job-created` (consumer group `"job-orchestrator"`, consumer name `"orchestrator-1"`).

For each event:
1. Load the `Job` from the job DB keyed by `event.ConnectionId`
2. Load online hosts for `job.HostGroupId`
3. If **no online hosts** → log warning and `continue` (job stays `Pending` — see Known Limitation below)
4. Select a host via `ILoadBalancer.Select(...)`
5. Transition job to `Started`, assign `HostId`, call `jobRepo.SaveAsync`
6. Publish `JobAssignedEvent` to `flowforge:host:{selectedHostId}`

On exception → `IDlqWriter.WriteAsync` + continue (never crashes the consumer).

Uses OpenTelemetry span: `"dispatch job {jobId}"`.

```csharp
// JobDispatcherWorker simplified
await foreach (var @event in consumer.ConsumeAsync<JobCreatedEvent>(
    StreamNames.JobCreated, "job-orchestrator", "orchestrator-1", stoppingToken))
{
    try
    {
        var jobRepo = serviceProvider.GetRequiredKeyedService<IJobRepository>(@event.ConnectionId);
        var job = await jobRepo.GetByIdAsync(@event.JobId, stoppingToken);

        var hosts   = await hostRepo.GetByGroupAsync(job.HostGroupId, stoppingToken);
        var online  = hosts.Where(h => h.IsOnline).ToList();
        if (online.Count == 0) { /* warn + continue */ continue; }

        var host = loadBalancer.Select(online, job.HostGroupId);

        job.Transition(JobStatus.Started);
        job.AssignToHost(host.Id);
        await jobRepo.SaveAsync(job, stoppingToken);

        await publisher.PublishAsync(
            new JobAssignedEvent(job.Id, @event.ConnectionId, host.Id, job.AutomationId, DateTimeOffset.UtcNow),
            StreamNames.HostStream(host.Id.ToString()), stoppingToken);
    }
    catch (Exception ex) { /* DLQ + continue */ }
}
```

### Known Limitation: No-Host Drop
When no hosts are online the job is silently acknowledged and left in `Pending` status. There is no re-queue mechanism. If hosts come back online the job will never be dispatched. This is tracked as ROADMAP item #4.

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
- Checks Redis key `host:heartbeat:{hostId}` (set by `HostHeartbeatWorker` in WorkflowHost with a 30-second TTL)
- Key **absent** + host is `IsOnline=true` → `host.MarkOffline()` + save
- Key **present** + host is `IsOnline=false` → `host.MarkOnline()` + save

```json
// appsettings.json
"HeartbeatMonitor": {
  "CheckIntervalSeconds": 15
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

---

## DI Registration (Program.cs)

```csharp
builder.Services.AddInfrastructure(builder.Configuration, "JobOrchestrator");
builder.Services.AddSingleton<ILoadBalancer, RoundRobinLoadBalancer>();

builder.Services.Configure<HeartbeatMonitorOptions>(
    builder.Configuration.GetSection(HeartbeatMonitorOptions.SectionName));

builder.Services.AddHostedService<JobDispatcherWorker>();
builder.Services.AddHostedService<HeartbeatMonitorWorker>();
```
