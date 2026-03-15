# JOBAUTOMATOR.md — Job Automator Service

## Responsibility

The Job Automator is a **Worker Service** that evaluates Automation trigger conditions and publishes `AutomationTriggeredEvent` when conditions are met.

**This service has no database access.** It operates entirely from an in-memory cache of Automations that is populated on startup and kept current via a Redis Stream. The database is owned by Web API — JobAutomator is a pure consumer of data, not a direct reader of it.

---

## Why No Direct DB Access?

The legacy approach (polling the Web API every 60 seconds, or reading DB directly) has two problems:

- **Polling the API** — creates unnecessary load, introduces up-to-60-second staleness, and creates tight coupling between two services at the HTTP level.
- **Reading the DB directly** — breaks the ownership boundary. If the Automation schema changes, JobAutomator silently breaks. The Web API is the single source of truth for Automation data.

**The solution: event-driven in-memory cache.**

JobAutomator initialises its cache from the API once on startup, then subscribes to a Redis Stream for all subsequent changes. The cache is always within milliseconds of the Web API's state — with zero polling.

---

## How It Works (Overview)

```
┌───────────────────────────────────────────────────────────────┐
│                       JobAutomator                             │
│                                                               │
│  [Startup]                                                    │
│   HTTP GET /api/automations → populate AutomationCache        │
│                                                               │
│  AutomationCacheSyncWorker        AutomationWorker            │
│  ──────────────────────────       ───────────────────────     │
│  Subscribe to                     For each automation         │
│  automation-changed stream        in AutomationCache:         │
│       ↓                            evaluate triggers          │
│  Update AutomationCache            check condition tree       │
│  (create / update / delete)        if met → publish event     │
│                                                               │
│  TriggerEvaluators (per type)                                 │
│  ─────────────────────────────                                │
│  Schedule  → Quartz.NET fires → sets Redis flag               │
│  SQL       → polls external DB (not FlowForge DB)             │
│  JobCompleted → consumes job-status-changed stream            │
│  Webhook   → reads Redis flag set by Web API                  │
└───────────────────────────────────────────────────────────────┘
```

---

## Automation Cache

### AutomationCache

A thread-safe in-memory store of all active Automations. This is the only data source the trigger evaluators and condition evaluator operate against.

```csharp
public sealed class AutomationCache
{
    private readonly ConcurrentDictionary<Guid, AutomationSnapshot> _automations = new();

    public void Seed(IEnumerable<AutomationSnapshot> automations)
    {
        _automations.Clear();
        foreach (var a in automations)
            _automations[a.Id] = a;
    }

    public void Upsert(AutomationSnapshot automation)
        => _automations[automation.Id] = automation;

    public void Remove(Guid automationId)
        => _automations.TryRemove(automationId, out _);

    public IReadOnlyList<AutomationSnapshot> GetAll()
        => [.. _automations.Values];
}
```

### AutomationSnapshot

A lightweight, immutable representation of an Automation — only the fields JobAutomator needs. Not a domain entity, not a DB model.

```csharp
public record AutomationSnapshot(
    Guid                        Id,
    bool                        IsActive,
    Guid                        HostGroupId,
    string                      ConnectionId,
    string                      TaskId,
    IReadOnlyList<TriggerSnapshot>   Triggers,
    TriggerConditionNode        ConditionRoot
);

public record TriggerSnapshot(
    Guid        Id,
    TriggerType Type,
    string      ConfigJson
);
```

---

## Startup: One-Time HTTP Snapshot

On startup, JobAutomator calls `GET /api/automations?includeAll=true` once to seed the cache. This is **not polling** — it is a single initialisation call.

```csharp
// In Program.cs (or a startup IHostedService that runs before AutomationWorker)
public class AutomationCacheInitializer(
    IAutomationApiClient apiClient,
    AutomationCache cache,
    ILogger<AutomationCacheInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        logger.LogInformation("Seeding automation cache from Web API...");
        var automations = await apiClient.GetAllAsync(ct);
        cache.Seed(automations);
        logger.LogInformation("Automation cache seeded with {Count} automations", automations.Count);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
```

### IAutomationApiClient

A typed HTTP client — the only external dependency this service has on the Web API:

```csharp
public interface IAutomationApiClient
{
    Task<IReadOnlyList<AutomationSnapshot>> GetAllAsync(CancellationToken ct);
}

// Registered with IHttpClientFactory:
// builder.Services.AddHttpClient<IAutomationApiClient, AutomationApiClient>(client =>
//     client.BaseAddress = new Uri(config["WebApi:BaseUrl"]!));
```

---

## AutomationCacheSyncWorker

Subscribes to `flowforge:automation-changed` stream and keeps the in-memory cache current. Web API publishes to this stream whenever an Automation is created, updated, or deleted.

```csharp
public class AutomationCacheSyncWorker(
    IMessageConsumer consumer,
    AutomationCache cache,
    ILogger<AutomationCacheSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var @event in consumer.ConsumeAsync<AutomationChangedEvent>(
            StreamNames.AutomationChanged,
            consumerGroup: "job-automator",
            consumerName:  "automator-cache-sync",
            ct:            stoppingToken))
        {
            switch (@event.ChangeType)
            {
                case ChangeType.Created or ChangeType.Updated:
                    cache.Upsert(@event.Automation!);
                    logger.LogDebug(
                        "Cache updated for automation {AutomationId} ({ChangeType})",
                        @event.AutomationId, @event.ChangeType);
                    break;

                case ChangeType.Deleted:
                    cache.Remove(@event.AutomationId);
                    logger.LogDebug("Cache removed automation {AutomationId}", @event.AutomationId);
                    break;
            }
        }
    }
}
```

### AutomationChangedEvent (in FlowForge.Contracts)

```csharp
public record AutomationChangedEvent(
    Guid                 AutomationId,
    ChangeType           ChangeType,
    AutomationSnapshot?  Automation   // null when ChangeType is Deleted
);

public enum ChangeType { Created, Updated, Deleted }
```

Web API publishes this event at the end of every `AutomationService.CreateAsync`, `UpdateAsync`, and `DeleteAsync`.

---

## AutomationWorker

The main evaluation loop. Iterates over the in-memory cache on a fixed interval, evaluates trigger conditions, and publishes `AutomationTriggeredEvent` when conditions are met.

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    while (!stoppingToken.IsCancellationRequested)
    {
        var automations = _cache.GetAll().Where(a => a.IsActive);

        foreach (var automation in automations)
        {
            // 1. Evaluate all triggers (each hits its own source — Quartz flag, Redis flag, etc.)
            var results = new Dictionary<Guid, bool>();
            foreach (var trigger in automation.Triggers)
            {
                var evaluator = _evaluators.Single(e => e.Type == trigger.Type);
                results[trigger.Id] = await evaluator.EvaluateAsync(trigger, stoppingToken);
            }

            // 2. Evaluate the AND/OR condition tree against trigger results
            var conditionMet = await _conditionEvaluator.EvaluateAsync(
                automation.ConditionRoot, results, stoppingToken);

            if (!conditionMet) continue;

            // 3. Publish — Web API will create the Job
            await _publisher.PublishAsync(new AutomationTriggeredEvent(
                AutomationId: automation.Id,
                HostGroupId:  automation.HostGroupId,
                ConnectionId: automation.ConnectionId,
                TaskId:       automation.TaskId,
                TriggeredAt:  DateTimeOffset.UtcNow
            ), stoppingToken);

            _logger.LogInformation("Automation {AutomationId} triggered", automation.Id);
        }

        await _redis.SetAsync(
            "automator:last-evaluated",
            DateTimeOffset.UtcNow.ToString("O"));

        await Task.Delay(_options.EvaluationIntervalMs, stoppingToken);
    }
}
```

> **Note on concurrency guard**: JobAutomator does not check whether a job is already running before publishing — that responsibility belongs to **Web API**. When Web API receives `AutomationTriggeredEvent`, it checks for an active job before creating a new one. This keeps the concurrency check close to the DB where the authoritative state lives.

---

## Trigger Types

### 1. Schedule Trigger

Quartz.NET registers a job per Automation on startup. When the schedule fires, it sets a short-lived Redis flag. The `AutomationWorker` reads and consumes the flag on its next evaluation pass.

```csharp
public class ScheduleTriggerEvaluator : ITriggerEvaluator
{
    public TriggerType Type => TriggerType.Schedule;

    public async Task<bool> EvaluateAsync(TriggerSnapshot trigger, CancellationToken ct)
    {
        var key   = $"trigger:schedule:{trigger.Id}:fired";
        var fired = await _redis.GetAsync(key);
        if (fired is null) return false;

        await _redis.DeleteAsync(key);   // consume — one fire per evaluation pass
        return true;
    }
}

// Quartz job (registered per automation trigger on startup and on cache update)
public class ScheduledTriggerJob(IRedisService redis) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var triggerId = context.JobDetail.JobDataMap.GetGuid("triggerId");
        await redis.SetAsync(
            $"trigger:schedule:{triggerId}:fired", "1",
            expiry: TimeSpan.FromMinutes(2));   // expires if evaluator misses a pass
    }
}
```

**Config shape (stored in `TriggerSnapshot.ConfigJson`):**
```json
{ "cronExpression": "0 0/5 * * * ?" }
```

### 2. SQL Trigger

Polls an **external, user-configured database** (not the FlowForge platform DB — this is fine). Tracks the last result hash in Redis to avoid re-firing on stale data.

```csharp
public class SqlTriggerEvaluator : ITriggerEvaluator
{
    public TriggerType Type => TriggerType.Sql;

    public async Task<bool> EvaluateAsync(TriggerSnapshot trigger, CancellationToken ct)
    {
        var config  = JsonSerializer.Deserialize<SqlTriggerConfig>(trigger.ConfigJson)!;
        var results = await ExecuteQueryAsync(config.ConnectionString, config.Query, ct);

        var hash     = ComputeHash(results);
        var lastHash = await _redis.GetAsync($"trigger:sql:{trigger.Id}:last-hash");

        if (hash == lastHash) return false;

        await _redis.SetAsync($"trigger:sql:{trigger.Id}:last-hash", hash);
        return results.Count > 0;
    }
}
```

**Config shape:**
```json
{
  "connectionString": "Host=external-db;Database=myapp;...",
  "query": "SELECT id FROM orders WHERE processed = false LIMIT 1",
  "pollingIntervalSeconds": 30
}
```

> SQL trigger polls a user's own database, not the FlowForge DB. No ownership boundary is crossed.

### 3. Job Completed Trigger

Consumes `flowforge:job-status-changed` stream. When a `Completed` event matches the configured `watchAutomationId`, it sets a Redis flag for the relevant triggers.

```csharp
public class JobCompletedTriggerEvaluator : ITriggerEvaluator
{
    public TriggerType Type => TriggerType.JobCompleted;

    // Called by a background stream consumer (JobCompletedFlagWorker), not during evaluate pass
    public async Task HandleJobStatusChangedAsync(JobStatusChangedEvent @event, CancellationToken ct)
    {
        if (@event.Status != JobStatus.Completed) return;

        // Find affected triggers from the in-memory cache (no DB lookup)
        var affectedTriggers = _cache.GetAll()
            .SelectMany(a => a.Triggers)
            .Where(t => t.Type == TriggerType.JobCompleted)
            .Where(t =>
            {
                var cfg = JsonSerializer.Deserialize<JobCompletedTriggerConfig>(t.ConfigJson)!;
                return cfg.WatchAutomationId == @event.AutomationId;
            });

        foreach (var trigger in affectedTriggers)
            await _redis.SetAsync(
                $"trigger:job-completed:{trigger.Id}:fired", "1",
                expiry: TimeSpan.FromMinutes(10));
    }

    public async Task<bool> EvaluateAsync(TriggerSnapshot trigger, CancellationToken ct)
    {
        var key   = $"trigger:job-completed:{trigger.Id}:fired";
        var fired = await _redis.GetAsync(key);
        if (fired is null) return false;

        await _redis.DeleteAsync(key);
        return true;
    }
}
```

**Config shape:**
```json
{ "watchAutomationId": "uuid-of-upstream-automation" }
```

### 4. Webhook Trigger

Web API sets a Redis flag when `POST /api/automations/{id}/webhook` is called. JobAutomator reads and consumes the flag.

```csharp
public class WebhookTriggerEvaluator : ITriggerEvaluator
{
    public TriggerType Type => TriggerType.Webhook;

    public async Task<bool> EvaluateAsync(TriggerSnapshot trigger, CancellationToken ct)
    {
        var key   = $"trigger:webhook:{trigger.Id}:fired";
        var fired = await _redis.GetAsync(key);
        if (fired is null) return false;

        await _redis.DeleteAsync(key);
        return true;
    }
}
```

---

## Trigger Condition Evaluator

Evaluates the AND/OR tree stored in `AutomationSnapshot.ConditionRoot` against a dictionary of per-trigger boolean results.

```csharp
public class TriggerConditionEvaluator
{
    public async Task<bool> EvaluateAsync(
        TriggerConditionNode node,
        IReadOnlyDictionary<Guid, bool> triggerResults,
        CancellationToken ct)
    {
        // Leaf node — maps directly to a trigger result
        if (node.TriggerId.HasValue)
            return triggerResults.GetValueOrDefault(node.TriggerId.Value);

        // Composite node — evaluate children then apply operator
        var childResults = await Task.WhenAll(
            node.Nodes!.Select(n => EvaluateAsync(n, triggerResults, ct)));

        return node.Operator switch
        {
            ConditionOperator.And => childResults.All(r => r),
            ConditionOperator.Or  => childResults.Any(r => r),
            _ => throw new ArgumentOutOfRangeException(nameof(node.Operator))
        };
    }
}
```

---

## Quartz Lifecycle: Syncing Schedules With Cache

When `AutomationCacheSyncWorker` updates the cache, it must also update Quartz jobs for any affected Schedule triggers.

```csharp
// AutomationCacheSyncWorker — after cache.Upsert(automation)
await _quartzScheduleSync.SyncAsync(automation, stoppingToken);

// QuartzScheduleSync
public async Task SyncAsync(AutomationSnapshot automation, CancellationToken ct)
{
    foreach (var trigger in automation.Triggers.Where(t => t.Type == TriggerType.Schedule))
    {
        var config = JsonSerializer.Deserialize<ScheduleTriggerConfig>(trigger.ConfigJson)!;
        var jobKey = new JobKey($"trigger-{trigger.Id}");

        // Delete existing, re-register with potentially new cron
        if (await _scheduler.CheckExists(jobKey, ct))
            await _scheduler.DeleteJob(jobKey, ct);

        if (automation.IsActive)
        {
            await _scheduler.ScheduleJob(
                JobBuilder.Create<ScheduledTriggerJob>()
                    .WithIdentity(jobKey)
                    .UsingJobData("triggerId", trigger.Id)
                    .Build(),
                TriggerBuilder.Create()
                    .WithCronSchedule(config.CronExpression)
                    .Build(),
                ct);
        }
    }
}
```

---

## Redis Keys Used by JobAutomator

| Key | TTL | Written by | Read by |
|---|---|---|---|
| `trigger:schedule:{triggerId}:fired` | 2 min | Quartz job | `ScheduleTriggerEvaluator` |
| `trigger:sql:{triggerId}:last-hash` | none | `SqlTriggerEvaluator` | `SqlTriggerEvaluator` |
| `trigger:job-completed:{triggerId}:fired` | 10 min | `JobCompletedFlagWorker` | `JobCompletedTriggerEvaluator` |
| `trigger:webhook:{triggerId}:fired` | 10 min | Web API | `WebhookTriggerEvaluator` |
| `automator:last-evaluated` | none | `AutomationWorker` | Monitoring / ops |

---

## What Web API Must Publish

For JobAutomator's cache to stay current, Web API must publish `AutomationChangedEvent` at the end of every automation mutation:

| Web API action | Event published |
|---|---|
| `POST /api/automations` | `AutomationChangedEvent` (Created) |
| `PUT /api/automations/{id}` | `AutomationChangedEvent` (Updated) |
| `DELETE /api/automations/{id}` | `AutomationChangedEvent` (Deleted) |

This is a one-line addition at the end of each service method — see **WEBAPI.md**.

---

## Configuration

```json
{
  "JobAutomator": {
    "EvaluationIntervalMs": 5000
  },
  "WebApi": {
    "BaseUrl": "http://webapi:8080"
  },
  "Redis": {
    "ConnectionString": "redis:6379"
  }
}
```

> No `ConnectionStrings:DefaultConnection` — JobAutomator does not connect to any FlowForge database.

---

## DI Registration (Program.cs sketch)

```csharp
// HTTP client for one-time startup snapshot
builder.Services
    .AddHttpClient<IAutomationApiClient, AutomationApiClient>(client =>
        client.BaseAddress = new Uri(builder.Configuration["WebApi:BaseUrl"]!));

// In-memory cache (singleton — shared across all workers)
builder.Services.AddSingleton<AutomationCache>();

// Quartz
builder.Services
    .AddQuartz(q => q.UseMicrosoftDependencyInjectionJobFactory())
    .AddQuartzHostedService();

// Trigger evaluators
builder.Services
    .AddScoped<ITriggerEvaluator, ScheduleTriggerEvaluator>()
    .AddScoped<ITriggerEvaluator, SqlTriggerEvaluator>()
    .AddScoped<ITriggerEvaluator, JobCompletedTriggerEvaluator>()
    .AddScoped<ITriggerEvaluator, WebhookTriggerEvaluator>();

builder.Services.AddScoped<TriggerConditionEvaluator>();

// Workers — order matters: initializer must finish before workers start
builder.Services
    .AddHostedService<AutomationCacheInitializer>()   // runs once at startup
    .AddHostedService<AutomationCacheSyncWorker>()    // keeps cache current
    .AddHostedService<AutomationWorker>();             // evaluation loop

builder.Services.AddInfrastructure(builder.Configuration);
```