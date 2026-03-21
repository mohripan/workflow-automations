# JOBAUTOMATOR.md — Job Automator Service

## Responsibility

The Job Automator is a worker service that:
1. Maintains an in-memory cache of all `Automation` configurations
2. Continuously evaluates trigger conditions for each enabled automation
3. Publishes `AutomationTriggeredEvent` to Redis when all conditions are met
4. Manages Quartz.NET scheduled jobs for `schedule`-type triggers

It has **no direct database writes** — all mutations go through the WebApi via Redis Stream events.

---

## Architecture Overview

```
WebApi (AutomationChangedEvent)
         │
         ▼
AutomationCacheSyncWorker ──► AutomationCache (in-memory, thread-safe)
                                      │
                              AutomationWorker (every N seconds)
                                      │
                              ITriggerEvaluator × N (one per TypeId)
                                      │
                              TriggerConditionEvaluator (AND/OR tree)
                                      │
                              IMessagePublisher ──► flowforge:automation-triggered
```

---

## AutomationCache

```csharp
public class AutomationCache
{
    public void Set(AutomationSnapshot snapshot);
    public void Remove(Guid automationId);
    public IReadOnlyList<AutomationSnapshot> GetAll();
}
```

Backed by a `ConcurrentDictionary<Guid, AutomationSnapshot>`. Snapshots are lightweight serialisable records that carry everything the evaluator needs:

```csharp
public record AutomationSnapshot(
    Guid Id,
    string Name,
    bool IsEnabled,
    Guid HostGroupId,
    string ConnectionId,
    string TaskId,
    IReadOnlyList<TriggerSnapshot> Triggers,
    TriggerConditionNode ConditionRoot,
    int? TimeoutSeconds = null,   // propagated to job on trigger
    int MaxRetries = 0,           // propagated to job on trigger
    string? TaskConfig = null);   // propagated to job on trigger — handler parameter JSON
```

---

## Startup — AutomationCacheInitializer

`AutomationCacheInitializer` runs once on startup. It calls `GET /api/automations` (via `IAutomationApiClient`) and hydrates the cache. Uses exponential backoff if the WebApi is not yet ready:

| Attempt | Delay |
|---|---|
| 1 | 2 s |
| 2 | 4 s |
| 3 | 8 s |
| 4+ | 16 s (capped) |

The initializer respects `CancellationToken` — it stops retrying when the host shuts down.

---

## Cache Sync — AutomationCacheSyncWorker

Subscribes to the `flowforge:automation-changed` Redis Stream (consumer group `"job-automator"`). On each `AutomationChangedEvent`:
- `Created` / `Updated` → `cache.Set(event.Snapshot)`
- `Deleted` → `cache.Remove(event.AutomationId)`

This keeps the in-memory cache consistent without polling the database.

---

## Main Loop — AutomationWorker

```csharp
while (!stoppingToken.IsCancellationRequested)
{
    await EvaluateAllAsync(stoppingToken);
    await redis.SetAsync("automator:last-evaluated", DateTimeOffset.UtcNow.ToString("O"));
    await Task.Delay(TimeSpan.FromSeconds(_options.EvaluationIntervalSeconds), stoppingToken);
}
```

Controlled by `AutomationWorkerOptions`:
```json
// appsettings.json
"AutomationWorker": {
  "EvaluationIntervalSeconds": 5
}
```

For each enabled automation:
1. Evaluate every trigger via its `ITriggerEvaluator`, building a `Dictionary<string, bool>` keyed by trigger name
2. Evaluate the `ConditionRoot` AND/OR tree against those results
3. If the root is `true`, publish `AutomationTriggeredEvent` with `TimeoutSeconds`, `MaxRetries`, and `TaskConfig` from the snapshot

Records `FlowForgeMetrics.TriggersFired` (tag: `automation_id`) on each triggered event.

---

## ITriggerEvaluator

```csharp
public interface ITriggerEvaluator
{
    string TypeId { get; }
    Task<bool> EvaluateAsync(TriggerSnapshot trigger, CancellationToken ct);
}
```

Registered as `IEnumerable<ITriggerEvaluator>` (multiple singletons). `AutomationWorker` resolves by `TypeId`. Unknown TypeIds log a warning and return `false` — they do not throw.

---

## Built-in Trigger Evaluators

### ScheduleTriggerEvaluator (`"schedule"`)
Delegates to Quartz.NET. Quartz fires a job that sets a boolean flag in Redis (`schedule:fired:{triggerId}`). The evaluator reads this flag and resets it. Config: `cronExpression`.

### SqlTriggerEvaluator (`"sql"`)
Executes a `SELECT` query against a configured external database. Stores a hash of the result in Redis; returns `true` when the result **changes** (hash differs from the previous run). This gives fire-once-on-transition semantics for boolean queries. Config: `connectionString` (AES-256-GCM encrypted — evaluator decrypts via `IEncryptionService` before connecting), `query`.

### JobCompletedTriggerEvaluator (`"job-completed"`)
Reads a Redis key set by `JobCompletedFlagWorker` when a job for the referenced automation completes. Returns `true` if the key exists and clears it. Config: `targetAutomationId`.

### WebhookTriggerEvaluator (`"webhook"`)
Returns `true` if a Redis key `webhook:fired:{triggerId}` exists (set by `POST /api/automations/{id}/triggers/{name}/webhook`). Clears the key on read. Config: `secretHash` (optional HMAC-SHA256 validation).

### CustomScriptTriggerEvaluator (`"custom-script"`)
Spawns a Python subprocess, captures its exit code. Exit 0 = true, any other exit = false. Supports `requirements.txt` and per-trigger virtualenvs. Config: `script`, `interpreter`, `requirementsTxt`, `timeoutSeconds`.

---

## TriggerConditionEvaluator

Evaluates the `TriggerConditionNode` AND/OR tree:
```csharp
public bool Evaluate(TriggerConditionNode node, Dictionary<string, bool> triggerResults)
{
    return node.Type switch
    {
        "trigger" => triggerResults.GetValueOrDefault(node.Name),
        "and"     => node.Children.All(c => Evaluate(c, triggerResults)),
        "or"      => node.Children.Any(c => Evaluate(c, triggerResults)),
        _         => false
    };
}
```

---

## Quartz Lifecycle

Quartz.NET runs in clustered mode using a PostgreSQL job store (`flowforge_quartz` database). Each Quartz trigger fires a job that writes a flag to Redis, which `ScheduleTriggerEvaluator` reads. This decouples Quartz from the evaluation loop.

Quartz scheduler ID is `"AUTO"`, scheduler name is `"FlowForgeScheduler"`. Clustering check-in interval: 10 seconds.

---

## Redis Keys

| Key | Writer | Reader | Description |
|---|---|---|---|
| `schedule:fired:{triggerId}` | Quartz job | ScheduleTriggerEvaluator | Cron trigger flag |
| `sql:result-hash:{triggerId}` | SqlTriggerEvaluator | SqlTriggerEvaluator | Hash of last query result; fires when changed |
| `job-completed:{automationId}` | JobCompletedFlagWorker | JobCompletedTriggerEvaluator | Job completion flag |
| `trigger:webhook:{triggerId}:fired` | AutomationsController | WebhookTriggerEvaluator | Webhook received flag (TTL: 10 min) |
| `automator:last-evaluated` | AutomationWorker | (monitoring) | Timestamp of last pass |

---

## Health Checks

```
GET /health/live   → 200 always (liveness)
GET /health/ready  → checks Redis
```

---

## Configuration

```json
{
  "AutomationWorker": {
    "EvaluationIntervalSeconds": 5
  },
  "Redis": {
    "ConnectionString": "localhost:6379"
  },
  "ConnectionStrings": {
    "QuartzConnection": "Host=localhost;Port=5435;Database=flowforge_quartz;Username=postgres;Password=postgres"
  },
  "WebApi": {
    "BaseUrl": "http://localhost:5015"
  },
  "OpenTelemetry": {
    "OtlpEndpoint": "http://localhost:4317"
  }
}
```

---

## DI Registration (Program.cs)

```csharp
builder.Services.AddRedis(builder.Configuration);
builder.Services.AddEncryption();  // IEncryptionService — used by SqlTriggerEvaluator to decrypt connection strings
builder.Services.AddFlowForgeTelemetry(builder.Configuration, "JobAutomator");

builder.Services.Configure<AutomationWorkerOptions>(
    builder.Configuration.GetSection(AutomationWorkerOptions.SectionName));
builder.Services.Configure<CustomScriptOptions>(
    builder.Configuration.GetSection(CustomScriptOptions.SectionName));

builder.Services.AddSingleton<AutomationCache>();
builder.Services.AddSingleton<IQuartzScheduleSync, QuartzScheduleSync>();
builder.Services.AddSingleton<TriggerConditionEvaluator>();
builder.Services.AddSingleton<ITriggerEvaluator, ScheduleTriggerEvaluator>();
builder.Services.AddSingleton<ITriggerEvaluator, SqlTriggerEvaluator>();
builder.Services.AddSingleton<ITriggerEvaluator, JobCompletedTriggerEvaluator>();
builder.Services.AddSingleton<ITriggerEvaluator, WebhookTriggerEvaluator>();
builder.Services.AddSingleton<ITriggerEvaluator, CustomScriptTriggerEvaluator>();

builder.Services.AddHostedService<AutomationCacheInitializer>();
builder.Services.AddHostedService<AutomationCacheSyncWorker>();
builder.Services.AddHostedService<JobCompletedFlagWorker>();
builder.Services.AddHostedService<AutomationWorker>();
```
