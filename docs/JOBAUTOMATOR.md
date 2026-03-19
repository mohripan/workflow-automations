# JOBAUTOMATOR.md — Job Automator Service

## Responsibility

The Job Automator is a **Worker Service** that evaluates Automation trigger conditions and publishes `AutomationTriggeredEvent` when conditions are met.

**This service has no database access.** It operates entirely from an in-memory cache of Automations populated on startup and kept current via a Redis Stream.

---

## Why No Direct DB Access?

- **Polling the API** — creates unnecessary load, staleness, and tight coupling.
- **Reading the DB directly** — breaks the ownership boundary; the Web API is the single source of truth.

**Solution: event-driven in-memory cache.** Seeded once from the API on startup, then kept current by a Redis Stream subscription.

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
│  Subscribe to                     For each ENABLED automation │
│  automation-changed stream        in AutomationCache:         │
│       ↓                            evaluate triggers          │
│  Update AutomationCache            check condition tree       │
│  (create / update / delete)        if met → publish event     │
│                                                               │
│  TriggerEvaluators (per TypeId)                               │
│  ─────────────────────────────                                │
│  "schedule"      → Quartz.NET fires → sets Redis flag         │
│  "sql"           → polls external DB                          │
│  "job-completed" → consumes job-status-changed stream         │
│  "webhook"       → reads Redis flag set by Web API            │
│  "custom-script" → runs Python subprocess on polling interval │
└───────────────────────────────────────────────────────────────┘
```

---

## Automation Cache

### AutomationCache

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

    public void Upsert(AutomationSnapshot automation) => _automations[automation.Id] = automation;
    public void Remove(Guid automationId) => _automations.TryRemove(automationId, out _);
    public IReadOnlyList<AutomationSnapshot> GetAll() => [.. _automations.Values];
}
```

### AutomationSnapshot

```csharp
public record AutomationSnapshot(
    Guid                             Id,
    string                           Name,
    bool                             IsEnabled,
    Guid                             HostGroupId,
    string                           ConnectionId,
    string                           TaskId,
    IReadOnlyList<TriggerSnapshot>   Triggers,
    TriggerConditionNode             ConditionRoot   // never null
);

public record TriggerSnapshot(
    Guid    Id,       // used for Redis key generation (internal)
    string  Name,     // used in condition expressions
    string  TypeId,   // e.g. "schedule", "custom-script" — matches TriggerTypes constants
    string  ConfigJson
);
```

---

## Startup: One-Time HTTP Snapshot

`AutomationCacheInitializer.StartAsync` retries the API call with exponential backoff (`[2, 4, 8, 16, 30]` seconds) until it succeeds or the host cancellation token fires. This prevents pod crash-loops in Kubernetes when the Web API is still starting up.

```csharp
public class AutomationCacheInitializer(
    IServiceScopeFactory scopeFactory,
    AutomationCache cache,
    IQuartzScheduleSync scheduleSync,
    ILogger<AutomationCacheInitializer> logger) : IHostedService
{
    private static readonly int[] BackoffSeconds = [2, 4, 8, 16, 30];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Initializing AutomationCache...");
        using var scope = scopeFactory.CreateScope();
        var apiClient = scope.ServiceProvider.GetRequiredService<IAutomationApiClient>();
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var snapshots = await apiClient.GetSnapshotsAsync(cancellationToken);
                foreach (var snapshot in snapshots)
                {
                    cache.Upsert(snapshot);
                    await scheduleSync.SyncAsync(snapshot, cancellationToken);
                }
                logger.LogInformation("AutomationCache initialized with {Count} automations.", snapshots.Count);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var delay = BackoffSeconds[Math.Min(attempt, BackoffSeconds.Length - 1)];
                logger.LogWarning(ex,
                    "AutomationCache initialization failed (attempt {Attempt}); retrying in {Delay}s.",
                    attempt + 1, delay);
                attempt++;
                try { await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

---

## AutomationCacheSyncWorker

Subscribes to `flowforge:automation-changed` and keeps the in-memory cache current.

```csharp
public class AutomationCacheSyncWorker(
    IMessageConsumer consumer,
    AutomationCache cache,
    QuartzScheduleSync quartzSync,
    ILogger<AutomationCacheSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("AutomationCacheSyncWorker started");

        await foreach (var @event in consumer.ConsumeAsync<AutomationChangedEvent>(
            StreamNames.AutomationChanged,
            consumerGroup: "job-automator",
            consumerName:  "automator-cache-sync",
            ct:            stoppingToken))
        {
            try { await HandleEventAsync(@event, stoppingToken); }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to process AutomationChangedEvent for automation {AutomationId}",
                    @event.AutomationId);
            }
        }
    }

    private async Task HandleEventAsync(AutomationChangedEvent @event, CancellationToken ct)
    {
        switch (@event.ChangeType)
        {
            case ChangeType.Created or ChangeType.Updated:
                cache.Upsert(@event.Automation!);
                await quartzSync.SyncAsync(@event.Automation!, ct);
                logger.LogInformation(
                    "Cache updated for automation {AutomationId} ({ChangeType}, IsEnabled={IsEnabled})",
                    @event.AutomationId, @event.ChangeType, @event.Automation!.IsEnabled);
                break;

            case ChangeType.Deleted:
                cache.Remove(@event.AutomationId);
                await quartzSync.RemoveAllAsync(@event.AutomationId, ct);
                logger.LogInformation(
                    "Cache removed automation {AutomationId}", @event.AutomationId);
                break;
        }
    }
}
```

---

## AutomationWorker

The main evaluation loop. Skips disabled automations. Resolves evaluators by `TypeId` string (not enum).

```csharp
public class AutomationWorker(
    AutomationCache cache,
    IEnumerable<ITriggerEvaluator> evaluators,
    TriggerConditionEvaluator conditionEvaluator,
    IMessagePublisher publisher,
    IRedisService redis,
    IOptions<JobAutomatorOptions> options,
    ILogger<AutomationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("AutomationWorker started (evaluation interval: {IntervalMs}ms)",
            options.Value.EvaluationIntervalMs);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EvaluateAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "AutomationWorker evaluation pass failed; retrying after delay");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            await redis.SetAsync("automator:last-evaluated", DateTimeOffset.UtcNow.ToString("O"));
            await Task.Delay(options.Value.EvaluationIntervalMs, stoppingToken);
        }

        logger.LogInformation("AutomationWorker stopped");
    }

    private async Task EvaluateAllAsync(CancellationToken ct)
    {
        var all     = cache.GetAll();
        var enabled = all.Where(a => a.IsEnabled).ToList();

        logger.LogDebug(
            "Evaluation pass: {EnabledCount} enabled, {DisabledCount} disabled",
            enabled.Count, all.Count - enabled.Count);

        foreach (var automation in enabled)
            await EvaluateAutomationAsync(automation, ct);
    }

    private async Task EvaluateAutomationAsync(AutomationSnapshot automation, CancellationToken ct)
    {
        if (automation.ConditionRoot is null)
        {
            logger.LogError(
                "Automation {AutomationId} ({Name}) has null ConditionRoot — skipping. " +
                "This is a data integrity issue.",
                automation.Id, automation.Name);
            return;
        }

        logger.LogDebug("Evaluating automation {AutomationId} ({Name})", automation.Id, automation.Name);

        // 1. Evaluate each trigger — resolve evaluator by TypeId (string)
        var results = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var trigger in automation.Triggers)
        {
            // Resolution is by TypeId string — no enum switch needed
            var evaluator = evaluators.SingleOrDefault(e => e.TypeId == trigger.TypeId);
            if (evaluator is null)
            {
                logger.LogWarning(
                    "No evaluator registered for TypeId '{TypeId}' " +
                    "(trigger '{TriggerName}' on automation {AutomationId}). Treating as false.",
                    trigger.TypeId, trigger.Name, automation.Id);
                results[trigger.Name] = false;
                continue;
            }

            results[trigger.Name] = await evaluator.EvaluateAsync(trigger, ct);

            logger.LogDebug(
                "Trigger '{TriggerName}' (type={TypeId}) on {AutomationId}: fired={Fired}",
                trigger.Name, trigger.TypeId, automation.Id, results[trigger.Name]);
        }

        // 2. Evaluate condition tree
        var conditionMet = conditionEvaluator.Evaluate(automation.ConditionRoot, results);

        logger.LogDebug(
            "Automation {AutomationId} ({Name}) condition result: {ConditionMet}",
            automation.Id, automation.Name, conditionMet);

        if (!conditionMet) return;

        // 3. Publish
        await publisher.PublishAsync(new AutomationTriggeredEvent(
            AutomationId: automation.Id,
            HostGroupId:  automation.HostGroupId,
            ConnectionId: automation.ConnectionId,
            TaskId:       automation.TaskId,
            TriggeredAt:  DateTimeOffset.UtcNow
        ), ct);

        logger.LogInformation(
            "Automation {AutomationId} ({Name}) triggered — event published",
            automation.Id, automation.Name);
    }
}
```

> **Concurrency guard**: the check for an already-running job lives in **Web API**, not here. This keeps the check close to the DB.

---

## ITriggerEvaluator

```csharp
public interface ITriggerEvaluator
{
    /// <summary>
    /// Matches TriggerTypes constants — e.g. "schedule", "custom-script".
    /// Used by AutomationWorker to resolve the right evaluator per trigger.
    /// </summary>
    string TypeId { get; }

    /// <summary>
    /// Returns true if this trigger fired since the last evaluation pass.
    /// Implementations consume the signal (e.g. delete Redis flag) to avoid double-firing.
    /// </summary>
    Task<bool> EvaluateAsync(TriggerSnapshot trigger, CancellationToken ct);
}
```

---

## Trigger Evaluators

### ScheduleTriggerEvaluator

```csharp
public class ScheduleTriggerEvaluator(IRedisService redis, ILogger<ScheduleTriggerEvaluator> logger)
    : ITriggerEvaluator
{
    public string TypeId => TriggerTypes.Schedule;

    public async Task<bool> EvaluateAsync(TriggerSnapshot trigger, CancellationToken ct)
    {
        var key   = $"trigger:schedule:{trigger.Id}:fired";
        var fired = await redis.GetAsync(key);
        if (fired is null) return false;

        await redis.DeleteAsync(key);
        logger.LogDebug("Schedule trigger '{TriggerName}' consumed fired flag", trigger.Name);
        return true;
    }
}
```

### SqlTriggerEvaluator

```csharp
public class SqlTriggerEvaluator(IRedisService redis, ILogger<SqlTriggerEvaluator> logger)
    : ITriggerEvaluator
{
    public string TypeId => TriggerTypes.Sql;

    public async Task<bool> EvaluateAsync(TriggerSnapshot trigger, CancellationToken ct)
    {
        var config  = JsonSerializer.Deserialize<SqlTriggerConfig>(trigger.ConfigJson)!;
        var results = await ExecuteQueryAsync(config.ConnectionString, config.Query, ct);
        var hash    = ComputeHash(results);
        var lastHash = await redis.GetAsync($"trigger:sql:{trigger.Id}:last-hash");

        if (hash == lastHash)
        {
            logger.LogDebug("SQL trigger '{TriggerName}' result unchanged", trigger.Name);
            return false;
        }

        await redis.SetAsync($"trigger:sql:{trigger.Id}:last-hash", hash);
        var fired = results.Count > 0;
        logger.LogDebug("SQL trigger '{TriggerName}' hash changed — rows={RowCount}, fired={Fired}",
            trigger.Name, results.Count, fired);
        return fired;
    }
}
```

### JobCompletedTriggerEvaluator

```csharp
public class JobCompletedTriggerEvaluator(
    IRedisService redis, AutomationCache cache,
    ILogger<JobCompletedTriggerEvaluator> logger) : ITriggerEvaluator
{
    public string TypeId => TriggerTypes.JobCompleted;

    // Called by JobCompletedFlagWorker (stream consumer) — not during the evaluate pass
    public async Task HandleJobStatusChangedAsync(JobStatusChangedEvent @event, CancellationToken ct)
    {
        if (@event.Status != JobStatus.Completed) return;

        var affected = cache.GetAll()
            .SelectMany(a => a.Triggers)
            .Where(t => t.TypeId == TriggerTypes.JobCompleted)
            .Where(t =>
            {
                var cfg = JsonSerializer.Deserialize<JobCompletedTriggerConfig>(t.ConfigJson)!;
                return cfg.WatchAutomationId == @event.AutomationId;
            })
            .ToList();

        foreach (var trigger in affected)
        {
            await redis.SetAsync(
                $"trigger:job-completed:{trigger.Id}:fired", "1",
                expiry: TimeSpan.FromMinutes(10));
            logger.LogInformation(
                "JobCompleted trigger '{TriggerName}' flagged — watched automation {WatchedId} completed",
                trigger.Name, @event.AutomationId);
        }
    }

    public async Task<bool> EvaluateAsync(TriggerSnapshot trigger, CancellationToken ct)
    {
        var key   = $"trigger:job-completed:{trigger.Id}:fired";
        var fired = await redis.GetAsync(key);
        if (fired is null) return false;
        await redis.DeleteAsync(key);
        logger.LogDebug("JobCompleted trigger '{TriggerName}' consumed fired flag", trigger.Name);
        return true;
    }
}
```

### WebhookTriggerEvaluator

```csharp
public class WebhookTriggerEvaluator(IRedisService redis, ILogger<WebhookTriggerEvaluator> logger)
    : ITriggerEvaluator
{
    public string TypeId => TriggerTypes.Webhook;

    public async Task<bool> EvaluateAsync(TriggerSnapshot trigger, CancellationToken ct)
    {
        var key   = $"trigger:webhook:{trigger.Id}:fired";
        var fired = await redis.GetAsync(key);
        if (fired is null) return false;
        await redis.DeleteAsync(key);
        logger.LogDebug("Webhook trigger '{TriggerName}' consumed fired flag", trigger.Name);
        return true;
    }
}
```

### CustomScriptTriggerEvaluator

Handles all triggers with `TypeId = "custom-script"`. Each trigger stores its own Python script in `ConfigJson`. See **TRIGGERS.md** for the full implementation including pip venv support and the security model.

```csharp
public class CustomScriptTriggerEvaluator(
    IRedisService redis,
    IOptions<CustomScriptOptions> options,
    ILogger<CustomScriptTriggerEvaluator> logger) : ITriggerEvaluator
{
    public string TypeId => TriggerTypes.CustomScript;

    public async Task<bool> EvaluateAsync(TriggerSnapshot trigger, CancellationToken ct)
    {
        var config = JsonSerializer.Deserialize<CustomScriptTriggerConfig>(trigger.ConfigJson)!;

        // Rate-limit: respect pollingIntervalSeconds per individual trigger
        var lastRunKey = $"trigger:custom-script:{trigger.Id}:last-run";
        var lastRunStr = await redis.GetAsync(lastRunKey);
        if (lastRunStr is not null)
        {
            var elapsed = DateTimeOffset.UtcNow - DateTimeOffset.Parse(lastRunStr);
            if (elapsed < TimeSpan.FromSeconds(config.PollingIntervalSeconds))
            {
                logger.LogDebug(
                    "Custom script trigger '{TriggerName}' skipped — within interval ({Interval}s)",
                    trigger.Name, config.PollingIntervalSeconds);
                return false;
            }
        }

        await redis.SetAsync(lastRunKey, DateTimeOffset.UtcNow.ToString("O"));

        // Run Python script in sandboxed subprocess — see TRIGGERS.md for full detail
        return await RunScriptAsync(trigger, config, ct);
    }

    // ... RunScriptAsync, EnsureVenvAsync — see TRIGGERS.md
}
```

---

## Trigger Condition Evaluator

Evaluates the AND/OR tree. **Dictionary is keyed by `TriggerName` (string), not by GUID.**

```csharp
public class TriggerConditionEvaluator(ILogger<TriggerConditionEvaluator> logger)
{
    public bool Evaluate(
        TriggerConditionNode node,
        IReadOnlyDictionary<string, bool> triggerResults)
    {
        if (node.TriggerName is not null)
        {
            if (!triggerResults.TryGetValue(node.TriggerName, out var result))
            {
                logger.LogWarning(
                    "Condition references TriggerName '{TriggerName}' with no evaluation result — treating as false",
                    node.TriggerName);
                return false;
            }
            return result;
        }

        var childResults = node.Nodes!.Select(n => Evaluate(n, triggerResults)).ToList();

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

## Quartz Lifecycle

`QuartzScheduleSync` syncs Quartz schedule jobs when the cache changes. It uses `trigger.TypeId == TriggerTypes.Schedule` (string comparison) instead of an enum switch. Disabling an automation removes its Quartz schedules; re-enabling re-registers them. See full implementation in **TRIGGERS.md**.

Quartz is configured with the **ADO.NET PostgreSQL job store** and clustering enabled. Only one node in the cluster fires each scheduled trigger regardless of replica count. `QuartzScheduleSync` requires no special handling — clustering is transparent to application code. The dedicated `flowforge_quartz` PostgreSQL database (port 5435) is provisioned by `deploy/docker/compose.yaml` and initialized with `quartz-postgresql.sql`.

---

## Redis Keys

| Key | TTL | Written by | Read by |
|---|---|---|---|
| `trigger:schedule:{id}:fired` | 2 min | `ScheduledTriggerJob` | `ScheduleTriggerEvaluator` |
| `trigger:sql:{id}:last-hash` | none | `SqlTriggerEvaluator` | `SqlTriggerEvaluator` |
| `trigger:job-completed:{id}:fired` | 10 min | `JobCompletedFlagWorker` | `JobCompletedTriggerEvaluator` |
| `trigger:webhook:{id}:fired` | 10 min | Web API | `WebhookTriggerEvaluator` |
| `trigger:custom-script:{id}:last-run` | none | `CustomScriptTriggerEvaluator` | `CustomScriptTriggerEvaluator` |
| `automator:last-evaluated` | none | `AutomationWorker` | Monitoring / ops |

All keys use the trigger's **GUID** (`trigger.Id`) for internal stability. `trigger.Name` appears only in condition expressions.

---

## What Web API Must Publish

| Web API action | Event |
|---|---|
| `POST /api/automations` | `AutomationChangedEvent` (Created) |
| `PUT /api/automations/{id}` | `AutomationChangedEvent` (Updated) |
| `DELETE /api/automations/{id}` | `AutomationChangedEvent` (Deleted) |
| `PUT /api/automations/{id}/enable` | `AutomationChangedEvent` (Updated, `IsEnabled=true`) |
| `PUT /api/automations/{id}/disable` | `AutomationChangedEvent` (Updated, `IsEnabled=false`) |

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
  },
  "CustomScript": {
    "ScriptTempDir": "/tmp/flowforge/scripts",
    "VenvCacheDir":  "/tmp/flowforge/venvs",
    "PythonPath":    "python3"
  }
}
```

---

## Redis Consumer Group Bootstrap

`Program.cs` calls `IStreamBootstrapper.EnsureAsync(streamName, groupName)` for every stream this service reads (`AutomationChanged` / `job-automator`, `JobStatusChanged` / `job-automator-flags`) before any `BackgroundService` starts consuming. The bootstrapper calls `XGROUP CREATE ... MKSTREAM $` and swallows `BUSYGROUP` errors — making it safe to call on every startup regardless of Redis state.

---

## DI Registration (Program.cs sketch)

```csharp
builder.Services
    .AddHttpClient<IAutomationApiClient, AutomationApiClient>(client =>
        client.BaseAddress = new Uri(builder.Configuration["WebApi:BaseUrl"]!));

builder.Services.AddSingleton<AutomationCache>();

builder.Services
    .AddQuartz(q => q.UseMicrosoftDependencyInjectionJobFactory())
    .AddQuartzHostedService();

builder.Services.AddSingleton<QuartzScheduleSync>();

builder.Services.Configure<CustomScriptOptions>(
    builder.Configuration.GetSection(CustomScriptOptions.SectionName));

// All evaluators — singleton, stateless (use Redis for any persistent state)
builder.Services
    .AddSingleton<ITriggerEvaluator, ScheduleTriggerEvaluator>()
    .AddSingleton<ITriggerEvaluator, SqlTriggerEvaluator>()
    .AddSingleton<ITriggerEvaluator, JobCompletedTriggerEvaluator>()
    .AddSingleton<ITriggerEvaluator, WebhookTriggerEvaluator>()
    .AddSingleton<ITriggerEvaluator, CustomScriptTriggerEvaluator>();

builder.Services.AddSingleton<TriggerConditionEvaluator>();

builder.Services
    .AddHostedService<AutomationCacheInitializer>()
    .AddHostedService<AutomationCacheSyncWorker>()
    .AddHostedService<AutomationWorker>()
    .AddHostedService<JobCompletedFlagWorker>();

builder.Services.AddInfrastructure(builder.Configuration);
```