# TRIGGERS.md — Trigger Type System & TriggersController

## Responsibility

This document covers:
- Why `TypeId` is a string, not an enum
- The `ITriggerTypeDescriptor` / `ITriggerTypeRegistry` system for self-describing triggers
- All built-in trigger types and their config schemas
- The `TriggersController` endpoints
- How new trigger types are added

---

## Why TypeId is a String

Using an enum for trigger types would require recompiling all services to add new types. Instead, `TypeId` is a plain string (`"schedule"`, `"sql"`, `"webhook"`, `"job-completed"`, `"custom-script"`).

Services that need to understand a trigger type register an `ITriggerTypeDescriptor` for it. Services that don't (e.g. WebApi stores triggers but doesn't evaluate them) treat `TypeId` as an opaque string.

---

## ITriggerTypeDescriptor

```csharp
public interface ITriggerTypeDescriptor
{
    string TypeId { get; }
    string DisplayName { get; }
    string? Description { get; }
    TriggerConfigSchema GetSchema();
    IReadOnlyList<string> ValidateConfig(string configJson);

    // Default implementation returns []. Override to declare fields that contain secrets.
    // AutomationService encrypts these fields at rest and redacts them to "***" in API responses.
    IReadOnlyList<string> GetSensitiveFieldNames() => [];
}

public record TriggerConfigSchema(
    string TypeId,
    string DisplayName,
    string? Description,
    IReadOnlyList<ConfigField> Fields);

public record ConfigField(
    string Name,
    string Label,
    ConfigFieldType DataType,      // enum: String, Int, Bool, CronExpression, MultilineString, Script, Enum, …
    bool Required,
    string? Description,
    string? DefaultValue,
    IReadOnlyList<string>? EnumValues);
```

---

## ITriggerTypeRegistry

```csharp
public interface ITriggerTypeRegistry
{
    void Register(ITriggerTypeDescriptor descriptor);
    IReadOnlyList<ITriggerTypeDescriptor> GetAll();
    ITriggerTypeDescriptor? Get(string typeId);
    bool IsKnown(string typeId);
}
```

Backed by `TriggerTypeRegistry` (in `FlowForge.Infrastructure`) which uses a `ConcurrentDictionary`. Registered as a singleton by `AddInfrastructure()`.

---

## Built-in Trigger Descriptors

### ScheduleTriggerDescriptor (`"schedule"`)
Config fields:
- `cronExpression` (text, required) — standard Quartz cron expression, e.g. `"0 0 9 * * ?"` (09:00 daily)

### SqlTriggerDescriptor (`"sql"`)
Config fields:
- `connectionString` (text, required) — connection string for the target external database
- `query` (textarea, required) — SELECT query; fires when the result changes from the previous evaluation
- `pollingIntervalSeconds` (number, optional, min 5, default 30) — polling cadence hint

**Sensitive fields:** `GetSensitiveFieldNames()` returns `["connectionString"]`. The connection string is AES-256-GCM encrypted before storage and redacted to `***` in all API responses. `SqlTriggerEvaluator` decrypts it at evaluation time.

### JobCompletedTriggerDescriptor (`"job-completed"`)
Config fields:
- `targetAutomationId` (text, required) — `Guid` of the automation whose job completion fires this trigger

### WebhookTriggerDescriptor (`"webhook"`)
Config fields:
- `secretHash` (text, optional) — BCrypt hash of the shared secret. If present, callers must pass the raw secret in the `X-Webhook-Secret` request header. Verified via `BCrypt.Net.BCrypt.Verify`. If absent, any caller may fire the webhook without a secret.

---

## Custom Script Trigger (`"custom-script"`)

The custom script trigger is special — it runs a Python script as a subprocess in the JobAutomator process to determine if the trigger should fire.

### CustomScriptTriggerDescriptor
Config fields:
- `script` (textarea, required) — Python script body; exit 0 = fire, non-zero = don't fire
- `interpreter` (text, optional, default: `"python3"`) — interpreter path
- `requirementsTxt` (textarea, optional) — pip requirements; a virtualenv is created per trigger if this is set
- `timeoutSeconds` (number, optional, default: 30) — subprocess timeout

### Virtualenv Isolation
If `requirementsTxt` is non-empty, `CustomScriptTriggerEvaluator` creates a virtualenv at `{tempDir}/flowforge-venvs/{triggerId}/` and runs `pip install -r requirements.txt` on first evaluation. Subsequent evaluations reuse the venv.

### Security Model
- Scripts run in the JobAutomator process's OS user context — not sandboxed
- Do not store secrets in `script` (they appear in the config stored in PostgreSQL)
- Use `timeoutSeconds` to prevent runaway scripts blocking the evaluation loop
- Each trigger's venv is isolated from others and from the system Python

### Rate Limiting
A Redis key `custom-script:last-run:{triggerId}` prevents a custom script from running more than once per evaluation interval.

---

## TriggersController

### `GET /api/triggers/types`
Returns all registered `ITriggerTypeDescriptor` schemas ordered by `displayName`:
```json
[
  {
    "typeId": "schedule",
    "displayName": "Cron Schedule",
    "description": "Fires on a cron expression",
    "fields": [...]
  }
]
```

### `GET /api/triggers/types/{typeId}`
Returns the schema for a single trigger type, or 404 if unknown.

### `POST /api/triggers/types/{typeId}/validate-config`
Body: `{ "configJson": "..." }`. Returns `{ typeId, isValid, errors[] }`. 422 if invalid, 404 if type unknown.

### `GET /api/automations/{id}/triggers`
Returns all triggers for an automation as `TriggerResponse[]`.

### `POST /api/automations/{id}/triggers`
Creates a trigger. Request body: `CreateTriggerRequest { Name, TypeId, ConfigJson }`.
- Validates `TypeId` exists in the registry
- Validates `ConfigJson` against the descriptor's schema

### `PUT /api/automations/{id}/triggers/{triggerId}`
Updates trigger config. Writes `AutomationChangedEvent` to outbox so the JobAutomator cache is updated.

### `DELETE /api/automations/{id}/triggers/{triggerId}`
Removes a trigger. Validates the trigger's name is not referenced in `Automation.ConditionRoot` before deletion.

---

## Adding a New Built-in Trigger Type

1. Create `MyTriggerDescriptor : ITriggerTypeDescriptor` in `FlowForge.Infrastructure/Triggers/Descriptors/`
2. Register it in `ServiceCollectionExtensions.AddTriggerTypeRegistry()`: `registry.Register(new MyTriggerDescriptor())`
3. Create `MyTriggerEvaluator : ITriggerEvaluator` in `FlowForge.JobAutomator`
4. Register as `services.AddSingleton<ITriggerEvaluator, MyTriggerEvaluator>()`
5. Add a constant to `TriggerTypes` static class in `FlowForge.Domain`
6. Document the config schema fields in this file

No existing services need to be modified — the registry and evaluator resolution are open/closed.

---

## Impact on JobAutomator

`AutomationWorker` iterates `IEnumerable<ITriggerEvaluator>`. Each evaluator is matched by `evaluator.TypeId == trigger.TypeId`. Unknown TypeIds log a warning and are treated as `false` — they do not crash the evaluation loop.

---

## Task Type Discovery (Analogous System)

Workflow task handlers (`send-email`, `http-request`, `run-script`) have a parallel discovery system:
- `ITaskTypeDescriptor` / `ITaskTypeRegistry` in `FlowForge.Domain/Tasks/`
- Descriptors in `FlowForge.Infrastructure/Tasks/Descriptors/`
- Endpoint: `GET /api/task-types` · `GET /api/task-types/{taskId}`

See `SPECS.md` → "Task Type Discovery" for full details.

---

## Impact on WebApi DTOs

`CreateTriggerRequest.ConfigJson` is validated against the `ITriggerTypeDescriptor` schema, then passed through `AutomationService.EncryptSensitiveFields` before storage. Fields named in `GetSensitiveFieldNames()` are AES-256-GCM encrypted in place. In API responses, those same fields are replaced with `"***"` by `RedactSensitiveFields`. The evaluator receives the encrypted `configJson` from the cache snapshot and decrypts at evaluation time.

> **JSON case sensitivity:** all trigger config JSON is stored and transported as camelCase (e.g. `cronExpression`, `connectionString`). Descriptor `ValidateConfig` methods and `QuartzScheduleSync` must use `JsonSerializerOptions { PropertyNameCaseInsensitive = true }` when deserializing — failure to do so silently drops values and causes incorrect behavior.
