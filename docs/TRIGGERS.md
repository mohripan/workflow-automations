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
    string Description { get; }
    TriggerConfigSchema ConfigSchema { get; }
}

public record TriggerConfigSchema(IReadOnlyList<ConfigField> Fields);

public record ConfigField(
    string Name,
    string Label,
    string Type,           // "text", "number", "textarea", "select", "boolean"
    bool Required,
    string? DefaultValue = null,
    string? HelpText = null);
```

---

## ITriggerTypeRegistry

```csharp
public interface ITriggerTypeRegistry
{
    IReadOnlyList<ITriggerTypeDescriptor> GetAll();
    ITriggerTypeDescriptor? Get(string typeId);
}
```

All descriptors are registered as `ITriggerTypeDescriptor` singletons and collected by `TriggerTypeRegistry`.

---

## Built-in Trigger Descriptors

### ScheduleTriggerDescriptor (`"schedule"`)
Config fields:
- `cronExpression` (text, required) — standard Quartz cron expression, e.g. `"0 0 9 * * ?"` (09:00 daily)

### SqlTriggerDescriptor (`"sql"`)
Config fields:
- `connectionString` (text, required) — JDBC-style connection string for the target database
- `query` (textarea, required) — SELECT query; fires if result set is non-empty

### JobCompletedTriggerDescriptor (`"job-completed"`)
Config fields:
- `targetAutomationId` (text, required) — `Guid` of the automation whose job completion fires this trigger

### WebhookTriggerDescriptor (`"webhook"`)
Config fields:
- `secretHash` (text, optional) — SHA-256 hex hash of the HMAC secret; if present, incoming webhook must include a valid `X-FlowForge-Signature` header

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

### `GET /api/trigger-types`
Returns all registered `ITriggerTypeDescriptor` instances as a list:
```json
[
  {
    "typeId": "schedule",
    "displayName": "Cron Schedule",
    "description": "Fires on a cron expression",
    "configSchema": { "fields": [...] }
  }
]
```

### `POST /api/trigger-types/{typeId}/validate`
Validates a `ConfigJson` object against the descriptor's schema. Returns 200 with a list of validation errors (empty = valid), or 404 if `typeId` is unknown.

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

1. Create `MyTriggerDescriptor : ITriggerTypeDescriptor` in `FlowForge.WebApi` (or `FlowForge.Infrastructure` if needed by multiple services)
2. Register as `services.AddSingleton<ITriggerTypeDescriptor, MyTriggerDescriptor>()`
3. Create `MyTriggerEvaluator : ITriggerEvaluator` in `FlowForge.JobAutomator`
4. Register as `services.AddSingleton<ITriggerEvaluator, MyTriggerEvaluator>()`
5. Add a constant to `TriggerTypes` static class in `FlowForge.Domain`
6. Document the config schema fields in this file

No existing services need to be modified — the registry and evaluator resolution are open/closed.

---

## Impact on JobAutomator

`AutomationWorker` iterates `IEnumerable<ITriggerEvaluator>`. Each evaluator is matched by `evaluator.TypeId == trigger.TypeId`. Unknown TypeIds log a warning and are treated as `false` — they do not crash the evaluation loop.

---

## Impact on WebApi DTOs

`CreateTriggerRequest.ConfigJson` is stored as-is in the `Trigger.ConfigJson` column (jsonb). The WebApi validates it against the `ITriggerTypeDescriptor.ConfigSchema` but does not parse it structurally — that is the evaluator's responsibility.
