# JOBAUTOMATOR.md — Job Automator Service

## Responsibility

The Job Automator is a **Worker Service** that continuously evaluates Automation definitions against their trigger conditions. When conditions are met, it publishes an `AutomationTriggeredEvent` to Redis Streams, which the Web API picks up to create a Job.

**This service does not create Jobs directly.** It only signals that a trigger has fired.

---

## How It Works (Overview)

```
┌─────────────────────────────────────────────────────────┐
│                   AutomationWorker                       │
│                                                          │
│  1. Load all active Automations from DB                  │
│  2. For each Automation:                                 │
│     a. Evaluate each Trigger independently              │
│     b. Combine results using TriggerConditionEvaluator  │
│     c. If condition met → publish AutomationTriggeredEvent│
│  3. Update LastChecked scalar in Redis                   │
└─────────────────────────────────────────────────────────┘
```

The service is **not a short-poller against the Web API**. It reads Automations directly from the database and publishes results to Redis Streams.

---

## Trigger Types

### 1. Schedule Trigger
Fires at a configured cron expression or interval.

- Implemented using **Quartz.NET**. Each `ScheduleTriggerEvaluator` registers a Quartz job per Automation on startup.
- When the schedule fires, it sets a flag in Redis (`trigger:schedule:{automationId}:fired = true`).
- `AutomationWorker` reads this flag during its evaluation pass.

```csharp
public class ScheduleTriggerEvaluator : ITriggerEvaluator
{
    public TriggerType Type => TriggerType.Schedule;

    public async Task<bool> EvaluateAsync(Trigger trigger, CancellationToken ct)
    {
        var key = $"trigger:schedule:{trigger.AutomationId}:fired";
        var fired = await _redis.GetAsync(key);
        if (fired is null) return false;

        await _redis.DeleteAsync(key);   // consume the flag
        return true;
    }
}
```

**Config shape:**
```json
{
  "TriggerConfig": {
    "CronExpression": "0 0/5 * * * ?"
  }
}
```

### 2. SQL Trigger
Fires when a SQL query returns at least one row (or a row count exceeds a threshold).

- Polls the configured database directly — not the Web API.
- Uses the connection string from the Automation's `TriggerConfig`.
- Tracks the last polled row via a `LastQueryHash` scalar to avoid re-firing on the same data.

```csharp
public class SqlTriggerEvaluator : ITriggerEvaluator
{
    public TriggerType Type => TriggerType.Sql;

    public async Task<bool> EvaluateAsync(Trigger trigger, CancellationToken ct)
    {
        var config = JsonSerializer.Deserialize<SqlTriggerConfig>(trigger.ConfigJson)!;
        var results = await ExecuteQueryAsync(config.ConnectionString, config.Query, ct);

        var hash = ComputeHash(results);
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
  "TriggerConfig": {
    "ConnectionString": "Host=...",
    "Query": "SELECT id FROM orders WHERE processed = false",
    "PollingIntervalSeconds": 30
  }
}
```

### 3. Job Completed Trigger
Fires when a specific other Job (or any Job from a specific Automation) completes successfully.

- Subscribes to the `flowforge:job-status-changed` Redis Stream.
- Looks for `JobStatus.Completed` events matching the configured `WatchAutomationId` or `WatchJobId`.
- Sets a flag in Redis that the `AutomationWorker` reads during its pass.

```csharp
public class JobCompletedTriggerEvaluator : ITriggerEvaluator
{
    public TriggerType Type => TriggerType.JobCompleted;

    // Called during background stream consumption, not during evaluate pass
    public async Task HandleJobStatusChangedAsync(JobStatusChangedEvent @event)
    {
        if (@event.Status != JobStatus.Completed) return;

        // Find all automations watching this job/automation and set flags
        var watchingTriggers = await _triggerRepo.GetByWatchedAutomationAsync(@event.AutomationId);
        foreach (var trigger in watchingTriggers)
            await _redis.SetAsync($"trigger:job-completed:{trigger.Id}:fired", "1", expiry: TimeSpan.FromMinutes(10));
    }

    public async Task<bool> EvaluateAsync(Trigger trigger, CancellationToken ct)
    {
        var key = $"trigger:job-completed:{trigger.Id}:fired";
        var fired = await _redis.GetAsync(key);
        if (fired is null) return false;

        await _redis.DeleteAsync(key);
        return true;
    }
}
```

### 4. Webhook Trigger
Fires when an HTTP POST is received at `/api/automations/{automationId}/webhook`.

- The Web API endpoint sets a Redis flag.
- `WebhookTriggerEvaluator` reads and consumes the flag, identical to the pattern above.
- Optionally validates a shared secret in the request header.

---

## Trigger Condition Evaluator

Supports logical expressions over trigger results, e.g.:

```
(Trigger1 AND Trigger2) OR Trigger3
```

### TriggerCondition Schema
Stored as a JSON tree on the Automation entity:

```json
{
  "operator": "Or",
  "nodes": [
    {
      "operator": "And",
      "nodes": [
        { "triggerId": "uuid-1" },
        { "triggerId": "uuid-2" }
      ]
    },
    { "triggerId": "uuid-3" }
  ]
}
```

### Evaluation Algorithm

```csharp
public class TriggerConditionEvaluator
{
    public async Task<bool> EvaluateAsync(
        TriggerConditionNode node,
        IReadOnlyDictionary<Guid, bool> triggerResults,
        CancellationToken ct)
    {
        // Leaf node
        if (node.TriggerId.HasValue)
            return triggerResults.GetValueOrDefault(node.TriggerId.Value);

        // Composite node
        var childResults = await Task.WhenAll(
            node.Nodes.Select(n => EvaluateAsync(n, triggerResults, ct)));

        return node.Operator switch
        {
            ConditionOperator.And => childResults.All(r => r),
            ConditionOperator.Or  => childResults.Any(r => r),
            _ => throw new ArgumentOutOfRangeException()
        };
    }
}
```

---

## AutomationWorker

The main loop. Runs continuously as a `BackgroundService`.

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    while (!stoppingToken.IsCancellationRequested)
    {
        var automations = await _automationRepo.GetAllActiveAsync(stoppingToken);

        foreach (var automation in automations)
        {
            // 1. Evaluate all triggers for this automation
            var results = new Dictionary<Guid, bool>();
            foreach (var trigger in automation.Triggers)
            {
                var evaluator = _evaluators.Single(e => e.Type == trigger.Type);
                results[trigger.Id] = await evaluator.EvaluateAsync(trigger, stoppingToken);
            }

            // 2. Evaluate the condition tree
            var conditionMet = await _conditionEvaluator.EvaluateAsync(
                automation.TriggerCondition.RootNode, results, stoppingToken);

            if (!conditionMet) continue;

            // 3. Check concurrency: don't fire if a job is already running
            var hasRunningJob = await _jobRepo.HasActiveJobForAutomationAsync(
                automation.Id, stoppingToken);
            if (hasRunningJob) continue;

            // 4. Publish event
            await _publisher.PublishAsync(new AutomationTriggeredEvent(
                AutomationId: automation.Id,
                HostGroupId:  automation.HostGroupId,
                TriggeredAt:  DateTimeOffset.UtcNow
            ), stoppingToken);

            _logger.LogInformation("Automation {AutomationId} triggered", automation.Id);
        }

        // Update last-checked scalar
        await _redis.SetAsync("automator:last-checked", DateTimeOffset.UtcNow.ToString("O"));

        await Task.Delay(_options.EvaluationIntervalMs, stoppingToken);
    }
}
```

---

## Scalars Tracked in Redis

| Key | Type | Description |
|---|---|---|
| `automator:last-checked` | String (ISO8601) | Timestamp of last evaluation pass |
| `trigger:schedule:{automationId}:fired` | String | Flag set by Quartz job |
| `trigger:sql:{triggerId}:last-hash` | String | Hash of last SQL result set |
| `trigger:job-completed:{triggerId}:fired` | String | Flag set by stream consumer |
| `trigger:webhook:{triggerId}:fired` | String | Flag set by WebApi controller |

---

## Configuration

```json
{
  "JobAutomator": {
    "EvaluationIntervalMs": 5000,
    "MaxConcurrentEvaluations": 10
  },
  "ConnectionStrings": {
    "DefaultConnection": "Host=postgres;Database=flowforge;..."
  },
  "Redis": {
    "ConnectionString": "redis:6379"
  }
}
```

---

## DI Registration (Program.cs sketch)

```csharp
builder.Services
    .AddInfrastructure(builder.Configuration)
    .AddQuartz(q => q.UseMicrosoftDependencyInjectionJobFactory())
    .AddQuartzHostedService()
    .AddScoped<ITriggerEvaluator, ScheduleTriggerEvaluator>()
    .AddScoped<ITriggerEvaluator, SqlTriggerEvaluator>()
    .AddScoped<ITriggerEvaluator, JobCompletedTriggerEvaluator>()
    .AddScoped<ITriggerEvaluator, WebhookTriggerEvaluator>()
    .AddScoped<TriggerConditionEvaluator>()
    .AddHostedService<AutomationWorker>();
```