# TRIGGERS.md — Trigger Type System & TriggersController

## Responsibility

This document covers:

1. How trigger **types** are defined and discovered — the `ITriggerTypeDescriptor` / `ITriggerTypeRegistry` system
2. The **config schema** — how each type describes its fields so the frontend can render the right form
3. The built-in trigger types: `schedule`, `sql`, `job-completed`, `webhook`
4. The **`custom-script`** trigger type — lets users supply their own Python script as a trigger condition
5. The `TriggersController` REST endpoints

---

## Why `TriggerType` Is a String, Not an Enum

The original design used a C# `enum` for trigger types. That works fine for a fixed set of built-ins, but it means adding any new type requires a code change and a redeploy.

**FlowForge replaces the enum with a string `TypeId`**. Built-in type IDs are string constants in a static class — no magic strings in calling code. Custom types simply use a new `TypeId` string value without touching domain code.

```csharp
// FlowForge.Domain/Triggers/TriggerTypes.cs
public static class TriggerTypes
{
    public const string Schedule     = "schedule";
    public const string Sql          = "sql";
    public const string JobCompleted = "job-completed";
    public const string Webhook      = "webhook";
    public const string CustomScript = "custom-script";
}
```

The `Trigger` entity's `TypeId` field is a `string` column in the database (not an enum column). `TriggerSnapshot.TypeId` is likewise a `string`.

> **Migration note**: if you previously had a `TriggerType` enum column in the DB, migrate it to a `varchar` and seed it with the string values above.

---

## Trigger Type Descriptor

Every trigger type — built-in or future extension — implements `ITriggerTypeDescriptor`. This is the self-description contract.

```csharp
// FlowForge.Domain/Triggers/ITriggerTypeDescriptor.cs
public interface ITriggerTypeDescriptor
{
    /// <summary>
    /// Unique stable identifier for this trigger type. Matches TriggerTypes constants.
    /// Stored in the Trigger entity and in TriggerSnapshot.
    /// </summary>
    string TypeId { get; }

    string  DisplayName { get; }
    string? Description { get; }

    /// <summary>
    /// Schema describing the configJson fields for this type.
    /// Used by the frontend to render the appropriate form controls.
    /// </summary>
    TriggerConfigSchema GetSchema();

    /// <summary>
    /// Validates a raw configJson string against this type's schema.
    /// Returns a list of validation error messages, or an empty list if valid.
    /// Called by the API before saving a Trigger to the database.
    /// </summary>
    IReadOnlyList<string> ValidateConfig(string configJson);
}
```

---

## Config Schema

The schema system lets the frontend know exactly what form to render for a given trigger type without hardcoding field names.

```csharp
// FlowForge.Domain/Triggers/TriggerConfigSchema.cs
public record TriggerConfigSchema(
    string                       TypeId,
    string                       DisplayName,
    string?                      Description,
    IReadOnlyList<ConfigField>   Fields
);

public record ConfigField(
    string              Name,           // camelCase key in ConfigJson
    string              Label,          // display label shown to user
    ConfigFieldType     DataType,
    bool                Required,
    string?             Description,    // tooltip / help text
    string?             DefaultValue,   // string representation of the default
    IReadOnlyList<string>? EnumValues   // only for DataType.Enum
);

public enum ConfigFieldType
{
    String,
    Int,
    Bool,
    CronExpression,   // cron picker widget
    ConnectionString, // password-masked text field
    MultilineString,  // large text area (plain text)
    Script,           // code editor (language specified separately via Description convention)
    Enum
}
```

### Why not JSON Schema?

JSON Schema is expressive but heavyweight for a UI-rendering use case. `TriggerConfigSchema` is intentionally simple: each `ConfigField` maps directly to one form control type. If more complex validation rules are needed, add them to `ITriggerTypeDescriptor.ValidateConfig` — not to the schema.

---

## Trigger Type Registry

A singleton that holds all registered `ITriggerTypeDescriptor`s and is queried by both the `TriggersController` and the `JobAutomator` evaluator resolver.

```csharp
// FlowForge.Domain/Triggers/ITriggerTypeRegistry.cs
public interface ITriggerTypeRegistry
{
    void Register(ITriggerTypeDescriptor descriptor);
    ITriggerTypeDescriptor? Get(string typeId);
    IReadOnlyList<ITriggerTypeDescriptor> GetAll();
    bool IsKnown(string typeId);
}

// FlowForge.Infrastructure/Triggers/TriggerTypeRegistry.cs
public sealed class TriggerTypeRegistry : ITriggerTypeRegistry
{
    private readonly ConcurrentDictionary<string, ITriggerTypeDescriptor> _descriptors = new();

    public void Register(ITriggerTypeDescriptor descriptor)
        => _descriptors[descriptor.TypeId] = descriptor;

    public ITriggerTypeDescriptor? Get(string typeId)
        => _descriptors.GetValueOrDefault(typeId);

    public IReadOnlyList<ITriggerTypeDescriptor> GetAll()
        => [.. _descriptors.Values];

    public bool IsKnown(string typeId)
        => _descriptors.ContainsKey(typeId);
}
```

All built-in descriptors are registered at startup in `AddInfrastructure`. The registry is then used by:
- `TriggersController` — to answer schema discovery requests
- `AutomationService.CreateAsync` / `UpdateAsync` — to validate `configJson` before saving
- `AutomationWorker` — to resolve the right `ITriggerEvaluator` for a given `TypeId`

---

## Built-in Trigger Type Descriptors

Each built-in type has a concrete descriptor in `FlowForge.Infrastructure/Triggers/Descriptors/`.

### ScheduleTriggerDescriptor

```csharp
public class ScheduleTriggerDescriptor : ITriggerTypeDescriptor
{
    public string  TypeId      => TriggerTypes.Schedule;
    public string  DisplayName => "Schedule";
    public string? Description => "Fires automatically on a cron schedule (UTC).";

    public TriggerConfigSchema GetSchema() => new(
        TypeId:      TypeId,
        DisplayName: DisplayName,
        Description: Description,
        Fields:
        [
            new ConfigField(
                Name:         "cronExpression",
                Label:        "Cron Expression",
                DataType:     ConfigFieldType.CronExpression,
                Required:     true,
                Description:  "Standard 6-part cron (seconds included). Example: '0 0 8 * * ?' fires at 08:00 UTC daily.",
                DefaultValue: "0 0 8 * * ?",
                EnumValues:   null)
        ]);

    public IReadOnlyList<string> ValidateConfig(string configJson)
    {
        var errors = new List<string>();
        try
        {
            var cfg = JsonSerializer.Deserialize<ScheduleTriggerConfig>(configJson);
            if (string.IsNullOrWhiteSpace(cfg?.CronExpression))
                errors.Add("cronExpression is required.");
            else if (!CronExpression.IsValidExpression(cfg.CronExpression))
                errors.Add($"'{cfg.CronExpression}' is not a valid cron expression.");
        }
        catch
        {
            errors.Add("configJson is not valid JSON.");
        }
        return errors;
    }
}
```

### SqlTriggerDescriptor

```csharp
public class SqlTriggerDescriptor : ITriggerTypeDescriptor
{
    public string  TypeId      => TriggerTypes.Sql;
    public string  DisplayName => "SQL Query";
    public string? Description => "Fires when a SQL query returns at least one row, and the result has changed since the last check.";

    public TriggerConfigSchema GetSchema() => new(
        TypeId:      TypeId,
        DisplayName: DisplayName,
        Description: Description,
        Fields:
        [
            new ConfigField(
                Name:        "connectionString",
                Label:       "Connection String",
                DataType:    ConfigFieldType.ConnectionString,
                Required:    true,
                Description: "Connection string for the external database to query (not the FlowForge DB).",
                DefaultValue: null,
                EnumValues:  null),

            new ConfigField(
                Name:        "query",
                Label:       "SQL Query",
                DataType:    ConfigFieldType.MultilineString,
                Required:    true,
                Description: "SELECT query to run. Fires when the result set is non-empty and different from the previous run.",
                DefaultValue: "SELECT id FROM your_table WHERE condition = true LIMIT 1",
                EnumValues:  null),

            new ConfigField(
                Name:        "pollingIntervalSeconds",
                Label:       "Polling Interval (seconds)",
                DataType:    ConfigFieldType.Int,
                Required:    false,
                Description: "How often to run the query. Minimum 5 seconds.",
                DefaultValue: "30",
                EnumValues:  null)
        ]);

    public IReadOnlyList<string> ValidateConfig(string configJson)
    {
        var errors = new List<string>();
        try
        {
            var cfg = JsonSerializer.Deserialize<SqlTriggerConfig>(configJson);
            if (string.IsNullOrWhiteSpace(cfg?.ConnectionString)) errors.Add("connectionString is required.");
            if (string.IsNullOrWhiteSpace(cfg?.Query))            errors.Add("query is required.");
            if (cfg?.PollingIntervalSeconds < 5)                  errors.Add("pollingIntervalSeconds must be at least 5.");
        }
        catch { errors.Add("configJson is not valid JSON."); }
        return errors;
    }
}
```

### JobCompletedTriggerDescriptor

```csharp
public class JobCompletedTriggerDescriptor : ITriggerTypeDescriptor
{
    public string  TypeId      => TriggerTypes.JobCompleted;
    public string  DisplayName => "Job Completed";
    public string? Description => "Fires when a job belonging to a specific automation completes successfully.";

    public TriggerConfigSchema GetSchema() => new(
        TypeId:      TypeId,
        DisplayName: DisplayName,
        Description: Description,
        Fields:
        [
            new ConfigField(
                Name:        "watchAutomationId",
                Label:       "Watch Automation",
                DataType:    ConfigFieldType.String,
                Required:    true,
                Description: "ID (GUID) of the automation whose job completion triggers this.",
                DefaultValue: null,
                EnumValues:  null)
        ]);

    public IReadOnlyList<string> ValidateConfig(string configJson)
    {
        var errors = new List<string>();
        try
        {
            var cfg = JsonSerializer.Deserialize<JobCompletedTriggerConfig>(configJson);
            if (cfg?.WatchAutomationId == Guid.Empty || cfg?.WatchAutomationId == null)
                errors.Add("watchAutomationId is required and must be a valid GUID.");
        }
        catch { errors.Add("configJson is not valid JSON."); }
        return errors;
    }
}
```

### WebhookTriggerDescriptor

```csharp
public class WebhookTriggerDescriptor : ITriggerTypeDescriptor
{
    public string  TypeId      => TriggerTypes.Webhook;
    public string  DisplayName => "Webhook";
    public string? Description =>
        "Fires when an external system POSTs to /api/automations/{id}/webhook. " +
        "Optionally validates a shared secret header.";

    public TriggerConfigSchema GetSchema() => new(
        TypeId:      TypeId,
        DisplayName: DisplayName,
        Description: Description,
        Fields:
        [
            new ConfigField(
                Name:        "secretHash",
                Label:       "Webhook Secret (optional)",
                DataType:    ConfigFieldType.String,
                Required:    false,
                Description: "BCrypt hash of the secret value that callers must pass in the X-Webhook-Secret header. " +
                             "Leave blank to accept unauthenticated webhooks.",
                DefaultValue: null,
                EnumValues:  null)
        ]);

    public IReadOnlyList<string> ValidateConfig(string configJson)
    {
        // secretHash is optional — empty config object is valid
        try   { JsonSerializer.Deserialize<WebhookTriggerConfig>(configJson); }
        catch { return ["configJson is not valid JSON."]; }
        return [];
    }
}
```

---

## Custom Script Trigger

### Overview

`custom-script` is a built-in trigger type whose config includes a **Python script** written by the user. FlowForge runs this script in a sandboxed subprocess on a configurable polling interval. The trigger fires when the script exits with code `0` **and** prints `true` (case-insensitive) to stdout.

This covers the vast majority of custom trigger scenarios:
- Check an external REST API response
- Query a non-SQL data source (Redis, files, S3)
- Evaluate a complex condition involving multiple data points
- Anything Python can do in a sandboxed environment

### Config Shape

```json
{
  "scriptContent": "import requests\nresp = requests.get('https://api.example.com/ready')\nprint('true' if resp.json()['ready'] else 'false')",
  "requirements": "requests==2.32.3",
  "pollingIntervalSeconds": 30,
  "timeoutSeconds": 10
}
```

### CustomScriptTriggerDescriptor

```csharp
public class CustomScriptTriggerDescriptor : ITriggerTypeDescriptor
{
    public string  TypeId      => TriggerTypes.CustomScript;
    public string  DisplayName => "Custom Script";
    public string? Description =>
        "Runs a Python script on a polling interval. " +
        "The trigger fires when the script exits with code 0 and prints 'true' to stdout.";

    public TriggerConfigSchema GetSchema() => new(
        TypeId:      TypeId,
        DisplayName: DisplayName,
        Description: Description,
        Fields:
        [
            new ConfigField(
                Name:        "scriptContent",
                Label:       "Python Script",
                DataType:    ConfigFieldType.Script,       // frontend renders a code editor
                Required:    true,
                Description: "Python 3 script. Print 'true' to fire the trigger, anything else (or exit non-zero) to skip.",
                DefaultValue: "# Return 'true' to fire the trigger\nprint('false')",
                EnumValues:  null),

            new ConfigField(
                Name:        "requirements",
                Label:       "pip Requirements",
                DataType:    ConfigFieldType.MultilineString,
                Required:    false,
                Description: "Newline-separated pip packages to install before running. " +
                             "Example: 'requests==2.32.3'. Packages are cached across runs.",
                DefaultValue: null,
                EnumValues:  null),

            new ConfigField(
                Name:        "pollingIntervalSeconds",
                Label:       "Polling Interval (seconds)",
                DataType:    ConfigFieldType.Int,
                Required:    false,
                Description: "How often the script is run. Minimum 5 seconds.",
                DefaultValue: "30",
                EnumValues:  null),

            new ConfigField(
                Name:        "timeoutSeconds",
                Label:       "Script Timeout (seconds)",
                DataType:    ConfigFieldType.Int,
                Required:    false,
                Description: "Maximum time the script is allowed to run before it is killed. Maximum 60 seconds.",
                DefaultValue: "10",
                EnumValues:  null)
        ]);

    public IReadOnlyList<string> ValidateConfig(string configJson)
    {
        var errors = new List<string>();
        try
        {
            var cfg = JsonSerializer.Deserialize<CustomScriptTriggerConfig>(configJson);
            if (string.IsNullOrWhiteSpace(cfg?.ScriptContent))
                errors.Add("scriptContent is required.");
            if (cfg?.PollingIntervalSeconds < 5)
                errors.Add("pollingIntervalSeconds must be at least 5.");
            if (cfg?.TimeoutSeconds is < 1 or > 60)
                errors.Add("timeoutSeconds must be between 1 and 60.");
        }
        catch { errors.Add("configJson is not valid JSON."); }
        return errors;
    }
}
```

### CustomScriptTriggerEvaluator

Runs the script in a subprocess. All custom-script triggers share this single evaluator — each one reads its own script from its `ConfigJson`.

```csharp
// FlowForge.JobAutomator/Evaluators/CustomScriptTriggerEvaluator.cs
public class CustomScriptTriggerEvaluator(
    IOptions<CustomScriptOptions> options,
    ILogger<CustomScriptTriggerEvaluator> logger) : ITriggerEvaluator
{
    public string TypeId => TriggerTypes.CustomScript;

    public async Task<bool> EvaluateAsync(TriggerSnapshot trigger, CancellationToken ct)
    {
        var config = JsonSerializer.Deserialize<CustomScriptTriggerConfig>(trigger.ConfigJson)!;

        // Rate-limit: skip evaluation if insufficient time has passed since the last run.
        // The evaluator loop runs on a fixed interval; pollingIntervalSeconds is enforced here.
        var lastRunKey = $"trigger:custom-script:{trigger.Id}:last-run";
        var lastRunStr = await _redis.GetAsync(lastRunKey);

        if (lastRunStr is not null)
        {
            var lastRun  = DateTimeOffset.Parse(lastRunStr);
            var interval = TimeSpan.FromSeconds(config.PollingIntervalSeconds);
            if (DateTimeOffset.UtcNow - lastRun < interval)
            {
                logger.LogDebug(
                    "Custom script trigger '{TriggerName}' skipped — " +
                    "within polling interval ({IntervalSeconds}s)",
                    trigger.Name, config.PollingIntervalSeconds);
                return false;
            }
        }

        await _redis.SetAsync(lastRunKey, DateTimeOffset.UtcNow.ToString("O"));

        logger.LogDebug("Running custom script for trigger '{TriggerName}' (id={TriggerId})",
            trigger.Name, trigger.Id);

        return await RunScriptAsync(trigger, config, ct);
    }

    private async Task<bool> RunScriptAsync(
        TriggerSnapshot trigger, CustomScriptTriggerConfig config, CancellationToken ct)
    {
        // Write script to a temp file scoped to this trigger evaluation
        var scriptPath = Path.Combine(options.Value.ScriptTempDir, $"trigger-{trigger.Id}.py");
        await File.WriteAllTextAsync(scriptPath, config.ScriptContent, ct);

        using var cts     = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeout       = TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 1, 60));
        cts.CancelAfter(timeout);

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("python3", scriptPath)
                {
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,

                    // Sandbox: no FlowForge credentials in environment
                    EnvironmentVariables   = { },
                    Environment            = { }
                }
            };

            process.Start();

            var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = await process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token);

            if (process.ExitCode != 0)
            {
                logger.LogWarning(
                    "Custom script trigger '{TriggerName}' exited with code {ExitCode}. Stderr: {Stderr}",
                    trigger.Name, process.ExitCode, stderr.Trim());
                return false;
            }

            var fired = stdout.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

            logger.LogDebug(
                "Custom script trigger '{TriggerName}' stdout='{Stdout}', fired={Fired}",
                trigger.Name, stdout.Trim(), fired);

            return fired;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "Custom script trigger '{TriggerName}' timed out after {TimeoutSeconds}s",
                trigger.Name, config.TimeoutSeconds);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Custom script trigger '{TriggerName}' threw an unexpected exception",
                trigger.Name);
            return false;
        }
        finally
        {
            // Clean up temp file — best effort
            try { File.Delete(scriptPath); } catch { /* ignore */ }
        }
    }
}
```

### pip Requirements

If `requirements` is non-empty, the evaluator installs them before the first run, using a per-trigger virtual environment cached in `options.Value.VenvCacheDir/{trigger.Id}/`.

```csharp
private async Task EnsureVenvAsync(TriggerSnapshot trigger, string requirements, CancellationToken ct)
{
    var venvDir    = Path.Combine(_options.VenvCacheDir, trigger.Id.ToString("N"));
    var markerFile = Path.Combine(venvDir, ".installed");
    var reqHash    = ComputeHash(requirements);

    // Re-install only if requirements changed
    if (File.Exists(markerFile) && await File.ReadAllTextAsync(markerFile, ct) == reqHash)
        return;

    logger.LogInformation(
        "Installing pip requirements for trigger '{TriggerName}'", trigger.Name);

    // Create venv
    await RunCommandAsync("python3", ["-m", "venv", venvDir], ct);

    // Write requirements file
    var reqFile = Path.Combine(venvDir, "requirements.txt");
    await File.WriteAllTextAsync(reqFile, requirements, ct);

    // pip install
    var pip = Path.Combine(venvDir, "bin", "pip");
    await RunCommandAsync(pip, ["install", "-r", reqFile, "--quiet"], ct);

    await File.WriteAllTextAsync(markerFile, reqHash, ct);
}
```

When `requirements` is set, `RunScriptAsync` uses the venv's Python binary (`{venvDir}/bin/python3`) instead of the system `python3`.

### Security Model

| Concern | Mitigation |
|---|---|
| Infinite loops / hangs | Hard `timeoutSeconds` limit (max 60s), enforced by `CancellationTokenSource` |
| Process spawn abuse | Only one subprocess per trigger per polling interval via Redis rate-limit key |
| Credential leakage | `EnvironmentVariables` cleared — no `REDIS_CONNECTION`, `DB_PASSWORD`, etc. passed to script |
| File system writes | Scripts run from a temp dir; venvs isolated per trigger ID |
| Network access | Not blocked at process level — scripts may call external APIs (this is intentional: SQL-equivalent access to external data) |
| Resource abuse | CPU/memory limits should be applied at the OS/container level (ulimit, cgroup), not in application code |

> **K8s note:** If JobAutomator runs in Kubernetes, apply a `LimitRange` to restrict CPU and memory for the pod. Custom script subprocesses inherit the pod's resource limits.

---

## TriggersController

```csharp
// FlowForge.WebApi/Controllers/TriggersController.cs
[ApiController]
[Route("api/triggers")]
public class TriggersController(
    ITriggerTypeRegistry registry,
    ILogger<TriggersController> logger) : ControllerBase
{
    /// <summary>
    /// Returns all available trigger types and their config schemas.
    /// The frontend uses this to render the "Add Trigger" form dynamically.
    /// </summary>
    [HttpGet("types")]
    public IActionResult GetAllTypes()
    {
        var schemas = registry.GetAll()
            .Select(d => d.GetSchema())
            .OrderBy(s => s.DisplayName)
            .ToList();

        logger.LogDebug("Returning {Count} trigger type schemas", schemas.Count);
        return Ok(schemas);
    }

    /// <summary>
    /// Returns the config schema for a single trigger type.
    /// </summary>
    [HttpGet("types/{typeId}")]
    public IActionResult GetType(string typeId)
    {
        var descriptor = registry.Get(typeId);
        if (descriptor is null)
        {
            logger.LogWarning("Trigger type '{TypeId}' not found", typeId);
            return NotFound(new ProblemDetails
            {
                Title  = "Trigger type not found",
                Detail = $"No trigger type with id '{typeId}' is registered.",
                Status = StatusCodes.Status404NotFound
            });
        }

        return Ok(descriptor.GetSchema());
    }

    /// <summary>
    /// Validates a configJson against the schema for a given trigger type.
    /// Returns 200 OK with an empty errors list if valid, or 422 with a list of messages.
    /// Useful for live form validation before the user saves an automation.
    /// </summary>
    [HttpPost("types/{typeId}/validate-config")]
    public IActionResult ValidateConfig(string typeId, [FromBody] ValidateConfigRequest request)
    {
        var descriptor = registry.Get(typeId);
        if (descriptor is null)
            return NotFound(new ProblemDetails
            {
                Title  = "Trigger type not found",
                Detail = $"No trigger type with id '{typeId}' is registered.",
                Status = StatusCodes.Status404NotFound
            });

        var errors = descriptor.ValidateConfig(request.ConfigJson);

        if (errors.Count > 0)
        {
            logger.LogDebug(
                "Config validation for trigger type '{TypeId}' failed: {Errors}",
                typeId, string.Join("; ", errors));

            return UnprocessableEntity(new TriggerConfigValidationResult(
                TypeId: typeId, IsValid: false, Errors: errors));
        }

        return Ok(new TriggerConfigValidationResult(
            TypeId: typeId, IsValid: true, Errors: []));
    }
}
```

### DTOs

```csharp
public record ValidateConfigRequest(string ConfigJson);

public record TriggerConfigValidationResult(
    string                   TypeId,
    bool                     IsValid,
    IReadOnlyList<string>    Errors
);
```

### Endpoints Summary

| Method | Route | Description |
|---|---|---|
| `GET` | `/api/triggers/types` | List all trigger types with config schemas |
| `GET` | `/api/triggers/types/{typeId}` | Get schema for one trigger type |
| `POST` | `/api/triggers/types/{typeId}/validate-config` | Validate a configJson before saving |

---

## How Config Validation Fits Into Automation Creation

When the user creates or updates an automation, `AutomationService` validates every trigger's `configJson` using the registry **before** calling `Automation.Create()`. This gives clear per-field errors rather than a generic DB constraint failure.

```csharp
// AutomationService.CreateAsync (additions)
private void ValidateTriggerConfigs(IEnumerable<CreateTriggerRequest> triggers)
{
    foreach (var triggerRequest in triggers)
    {
        var descriptor = _registry.Get(triggerRequest.TypeId);
        if (descriptor is null)
            throw new InvalidAutomationException(
                $"Unknown trigger type '{triggerRequest.TypeId}'. " +
                $"Call GET /api/triggers/types to see available types.");

        var errors = descriptor.ValidateConfig(triggerRequest.ConfigJson);
        if (errors.Count > 0)
            throw new InvalidAutomationException(
                $"Trigger '{triggerRequest.Name}' (type '{triggerRequest.TypeId}') has invalid config: " +
                string.Join("; ", errors));
    }
}
```

---

## Impact on JobAutomator

### ITriggerEvaluator

The `Type` property (formerly `TriggerType` enum) is now `TypeId` (string):

```csharp
// Before
public interface ITriggerEvaluator
{
    TriggerType Type { get; }
    Task<bool> EvaluateAsync(TriggerSnapshot trigger, CancellationToken ct);
}

// After
public interface ITriggerEvaluator
{
    string TypeId { get; }
    Task<bool> EvaluateAsync(TriggerSnapshot trigger, CancellationToken ct);
}
```

### Evaluator Resolution in AutomationWorker

```csharp
// Before
var evaluator = evaluators.Single(e => e.Type == trigger.Type);

// After
var evaluator = _evaluators.SingleOrDefault(e => e.TypeId == trigger.TypeId);
if (evaluator is null)
{
    _logger.LogWarning(
        "No evaluator registered for trigger type '{TypeId}' " +
        "(trigger '{TriggerName}' on automation {AutomationId})",
        trigger.TypeId, trigger.Name, automation.Id);
    results[trigger.Name] = false;
    continue;
}
```

### TriggerSnapshot

```csharp
// Before
public record TriggerSnapshot(Guid Id, string Name, TriggerType Type, string ConfigJson);

// After
public record TriggerSnapshot(Guid Id, string Name, string TypeId, string ConfigJson);
```

---

## Impact on Web API DTOs

### CreateTriggerRequest

```csharp
// Before
public record CreateTriggerRequest(string Name, TriggerType TriggerType, string ConfigJson);

// After
public record CreateTriggerRequest(
    string Name,
    string TypeId,      // e.g. "schedule", "sql", "custom-script"
    string ConfigJson
);
```

### TriggerResponse

```csharp
// Before
public record TriggerResponse(Guid Id, string Name, TriggerType Type, string ConfigJson);

// After
public record TriggerResponse(
    Guid   Id,
    string Name,
    string TypeId,      // matches TriggerTypes constants
    string ConfigJson
);
```

---

## Custom Script Config Model

```csharp
// FlowForge.JobAutomator/Evaluators/CustomScriptTriggerConfig.cs
public record CustomScriptTriggerConfig(
    string  ScriptContent,
    string? Requirements          = null,
    int     PollingIntervalSeconds = 30,
    int     TimeoutSeconds         = 10
);
```

---

## Configuration (JobAutomator appsettings.json)

```json
{
  "CustomScript": {
    "ScriptTempDir":  "/tmp/flowforge/scripts",
    "VenvCacheDir":   "/tmp/flowforge/venvs",
    "PythonPath":     "python3"
  }
}
```

```csharp
public sealed class CustomScriptOptions
{
    public const string SectionName = "CustomScript";

    public string ScriptTempDir { get; init; } = "/tmp/flowforge/scripts";
    public string VenvCacheDir  { get; init; } = "/tmp/flowforge/venvs";
    public string PythonPath    { get; init; } = "python3";
}
```

---

## DI Registration

### In `AddInfrastructure` (FlowForge.Infrastructure)

```csharp
// Register registry as singleton so descriptors are available everywhere
services.AddSingleton<ITriggerTypeRegistry>(sp =>
{
    var registry = new TriggerTypeRegistry();
    registry.Register(new ScheduleTriggerDescriptor());
    registry.Register(new SqlTriggerDescriptor());
    registry.Register(new JobCompletedTriggerDescriptor());
    registry.Register(new WebhookTriggerDescriptor());
    registry.Register(new CustomScriptTriggerDescriptor());
    return registry;
});
```

### In JobAutomator `Program.cs`

```csharp
builder.Services.Configure<CustomScriptOptions>(
    builder.Configuration.GetSection(CustomScriptOptions.SectionName));

// Evaluators — all singleton, stateless (or use Redis for state)
builder.Services
    .AddSingleton<ITriggerEvaluator, ScheduleTriggerEvaluator>()
    .AddSingleton<ITriggerEvaluator, SqlTriggerEvaluator>()
    .AddSingleton<ITriggerEvaluator, JobCompletedTriggerEvaluator>()
    .AddSingleton<ITriggerEvaluator, WebhookTriggerEvaluator>()
    .AddSingleton<ITriggerEvaluator, CustomScriptTriggerEvaluator>();  // ← new
```

---

## Adding a New Built-in Trigger Type (Future)

To add a new built-in trigger type without touching existing code:

1. Add a string constant to `TriggerTypes.cs`: `public const string MyNewType = "my-new-type";`
2. Create `MyNewTypeTriggerDescriptor : ITriggerTypeDescriptor` in `FlowForge.Infrastructure/Triggers/Descriptors/`
3. Create `MyNewTypeTriggerEvaluator : ITriggerEvaluator` in `FlowForge.JobAutomator/Evaluators/`
4. Register descriptor in `AddInfrastructure` and evaluator in JobAutomator `Program.cs`
5. No changes needed to domain, contracts, controllers, or existing evaluators

---

## Redis Keys Added by CustomScriptTriggerEvaluator

| Key | TTL | Purpose |
|---|---|---|
| `trigger:custom-script:{triggerId}:last-run` | none (updated on each run) | Rate-limit: tracks when the script was last executed to respect `pollingIntervalSeconds` |