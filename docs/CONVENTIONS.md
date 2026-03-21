# CONVENTIONS.md — Architecture & Coding Conventions

This document is the authoritative reference for how FlowForge code is written. Every contribution must follow these conventions. When in doubt, favour consistency with existing code over personal preference.

---

## Guiding Principles

1. **Domain is king** — business logic lives in `FlowForge.Domain`, nowhere else.
2. **Dependencies flow inward** — Domain → nothing. Contracts → nothing. Infrastructure → Domain + Contracts. Services → all of the above.
3. **Events are immutable contracts** — `record` types in `FlowForge.Contracts`. Never add mutable state to events.
4. **Shared infrastructure, not shared logic** — `FlowForge.Infrastructure` provides generic building blocks; service-specific logic stays in the service project.
5. **Explicit I/O at the boundaries** — every Redis, database, and HTTP call goes through an injected abstraction.

---

## Project Layer Rules

| Project | May depend on | Must NOT depend on |
|---|---|---|
| `FlowForge.Domain` | nothing | everything |
| `FlowForge.Contracts` | nothing | everything |
| `FlowForge.Infrastructure` | Domain, Contracts | Service projects |
| Service projects | All shared projects | Other service projects |

---

## Domain-Driven Design Rules

- **Entities** have `Id : Guid` and `UpdatedAt : DateTimeOffset` set on every mutation. All mutating methods are on the entity itself. Private setters only.
- **Aggregates** expose static factory methods (`Create(...)`) that guard invariants. Callers may not set properties directly.
- **Value Objects** are `readonly record struct` or `readonly struct`.
- **Domain Exceptions** extend `DomainException`. Thrown from entities or aggregates, not from services.

---

## Generic Patterns

### Message Publisher
```csharp
public interface IMessagePublisher
{
    Task PublishAsync<TEvent>(TEvent @event, string? streamOverride = null, CancellationToken ct = default)
        where TEvent : class;
}
```
`RedisStreamPublisher` serialises to JSON and appends a `traceparent` field for distributed trace propagation.

### Message Consumer
```csharp
public interface IMessageConsumer
{
    IAsyncEnumerable<TEvent> ConsumeAsync<TEvent>(
        string streamName, string consumerGroup, string consumerName,
        CancellationToken ct) where TEvent : class;
}
```
`RedisStreamConsumer` wraps `yield return` in `try/finally` so **XACK is always called** — even when the caller throws. Null/undeserializable entries are acked and skipped immediately.

### Repository Pattern
```csharp
public interface IRepository<T> where T : class
{
    Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task SaveAsync(T entity, CancellationToken ct = default);
}
```
`SaveAsync` calls `SaveChangesAsync` — it commits whatever the caller has staged on the context. Use this to bundle an entity mutation with an outbox write in one transaction.

### Multi-Database (Keyed Services)
Job databases are registered as `IJobRepository` keyed by `ConnectionId`:
```csharp
services.AddKeyedScoped<IJobRepository>("wf-jobs-minion", (sp, _) => new JobRepository(sp, "wf-jobs-minion"));
```
Always resolve with `GetRequiredKeyedService<IJobRepository>(connectionId)`. Never inject `IJobRepository` without a key.

### Redis Service
```csharp
public interface IRedisService
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value, TimeSpan? expiry = null);
    Task DeleteAsync(string key);
}
```

### Transactional Outbox Pattern
Use `IOutboxWriter` to publish Redis Stream messages atomically with a database mutation:
```csharp
// Both calls within the same DI scope — no SaveChangesAsync between them
await outboxWriter.WriteAsync(new SomeEvent(...), stoppingToken);  // stages OutboxMessage row
await someRepo.SaveAsync(entity, stoppingToken);  // commits entity + outbox row in one transaction
```
`OutboxRelayWorker` (in WebApi) polls `OutboxMessages` where `SentAt IS NULL` and publishes to Redis.

### Dead Letter Queue (DLQ)
All `BackgroundService` consumers that process Redis Stream events must inject `IDlqWriter` and follow this pattern:
```csharp
await foreach (var @event in consumer.ConsumeAsync<TEvent>(stream, group, name, stoppingToken))
{
    try
    {
        // ... processing ...
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to process {EventType}. Sending to DLQ.", typeof(TEvent).Name);
        await dlqWriter.WriteAsync(
            sourceStream: stream,
            messageId: @event.SomeId.ToString(),
            payload: JsonSerializer.Serialize(@event),
            error: ex.Message);
    }
}
```
`DlqWriter.WriteAsync` never throws — a failed DLQ write is logged and swallowed. XACK is always guaranteed by `RedisStreamConsumer`'s `try/finally`, regardless of whether the DLQ write succeeds.

DLQ entries are inspectable via `GET /api/dlq`, replayable via `POST /api/dlq/{id}/replay`, and deletable via `DELETE /api/dlq/{id}`.

### Stream Names
All stream name strings must come from the `StreamNames` static class. Never inline string literals for streams.

### IOptions for Configurable Settings
Worker timing constants must never be hardcoded. Use strongly-typed options classes:
```csharp
public class MyWorkerOptions
{
    public const string SectionName = "MyWorker";
    public int IntervalSeconds { get; init; } = 30;  // safe in-code default
}
```
Register in `Program.cs`:
```csharp
builder.Services.Configure<MyWorkerOptions>(builder.Configuration.GetSection(MyWorkerOptions.SectionName));
```
Inject as `IOptions<MyWorkerOptions>` (not `IOptionsMonitor` — polling intervals are read once at startup):
```csharp
public class MyWorker(IOptions<MyWorkerOptions> options) : BackgroundService
{
    private readonly MyWorkerOptions _options = options.Value;
}
```
Always add the section with its default value to `appsettings.json` so the knob is visible and documented.

### Health Checks
All services expose two endpoints:
- `GET /health/live` — liveness: `Predicate = _ => false` (always 200; only verifies the process is responsive)
- `GET /health/ready` — readiness: checks all external dependencies (PostgreSQL via `AddNpgSql`, Redis via `AddRedis`)

```csharp
app.MapHealthChecks("/health/live",  new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = _ => true  });
```

Never check external dependencies in the liveness probe — a temporary Redis outage should mark the pod not-ready, not dead.

### OpenTelemetry
All services call `builder.Services.AddFlowForgeTelemetry(config, "ServiceName")` which registers both tracing and metrics pipelines. Custom spans use `FlowForgeActivitySources`; custom metrics use `FlowForgeMetrics` statics. Never create `ActivitySource` or `Meter` instances outside these two classes — doing so creates unseen orphan instruments.

---

## Coding Conventions

### Naming
- Classes, interfaces, records, properties, methods: `PascalCase`
- Local variables, parameters: `camelCase`
- Private fields: `_camelCase`
- Constants and `const` fields: `PascalCase`
- Async methods: suffix with `Async`

### Async
- All I/O methods are `async Task` or `async Task<T>`.
- Always pass `CancellationToken ct` as the last parameter. Never use `CancellationToken.None` in production paths unless at the very end of execution (e.g. final status report after cancellation).
- Never call `.Result` or `.Wait()`.

### Nullable Reference Types
- All projects have `<Nullable>enable</Nullable>`.
- Use `?` annotations deliberately. `null!` is a code smell — prefer a factory method or `ArgumentNullException.ThrowIfNull`.

### Error Handling
- Catch specific exceptions, not bare `Exception`, except in top-level worker loops and exception-handling middleware.
- Worker outer loops: catch `OperationCanceledException` → break; catch `Exception` → log + short delay + continue.
- Never swallow exceptions silently — always log at `Error` or write to DLQ.

### Dependency Injection
- Prefer constructor injection.
- Singletons must be thread-safe.
- Use `IServiceScopeFactory` in background workers that need scoped services (repositories, DbContext).
- `HttpClient` always via `IHttpClientFactory`.

### Worker Services
- Extend `BackgroundService`, implement `ExecuteAsync`.
- Do not hold open database connections across loop iterations — create a new scope each pass.

### Logging
- Structured logging: `logger.LogInformation("Job {JobId} created", job.Id)` — never string interpolation in the message template.
- `Debug` — per-iteration trace; `Information` — significant state changes; `Warning` — degraded-but-operational; `Error` — failures.
- Do not log passwords, full connection strings, or full event payloads at `Information` or above.

### Configuration
- All configuration is read via `IConfiguration` or `IOptions<T>`.
- The only exception is WorkflowEngine, which reads `JOB_ID`, `JOB_AUTOMATION_ID`, and `CONNECTION_ID` from environment variables at process entry — these are set by `NativeProcessManager` when spawning the child process. All other config (Redis, database connections, SMTP) is inherited from the parent's environment.
- Throw `InvalidOperationException` at startup for missing required config, not at first use.

---

## Testing Conventions

- **Unit tests** (`FlowForge.Domain.Tests`) — no infrastructure, no containers, no network. Pure logic.
- **Integration tests** (`FlowForge.Integration.Tests`) — real PostgreSQL and Redis via Testcontainers.
- Test class names match the class under test: `TriggerConditionEvaluatorTests`.
- Use `FluentAssertions` for readable assertions.
- Use `NSubstitute` for mocks/stubs.
- Integration tests must not mock the database — Testcontainers only.
- Roll back DB state after each integration test using an `IDbContextTransaction` that is never committed.

---

## Git Conventions

- Branch names: `feature/<short-description>`, `fix/<short-description>`
- Commit messages: imperative, present tense ("Add retry logic", not "Added retry logic")
- One logical change per commit
- Never commit secrets, generated files (`bin/`, `obj/`, `.vs/`), or environment-specific overrides
