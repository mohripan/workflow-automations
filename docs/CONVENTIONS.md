# CONVENTIONS.md — Architecture & Coding Conventions

## Guiding Principles

1. **Domain is king** — business logic lives in `FlowForge.Domain`, nowhere else.
2. **Depend inward** — services depend on shared libraries, never on each other.
3. **Make shared things generic** — anything used by 2+ services belongs in `Infrastructure` behind an interface.
4. **Events are contracts** — never pass domain entities across service boundaries; use events from `FlowForge.Contracts`.
5. **No surprise I/O** — every database call, Redis call, or HTTP call must go through an injected abstraction.

---

## Domain-Driven Design (DDD)

### Entities
- All entities inherit from `BaseEntity<TId>` in `FlowForge.Domain`.
- Entities own their invariants. Example: `Job` exposes `Transition(JobStatus next)` which validates the state machine — callers do not set `Status` directly.
- Never expose `set` on entity properties from outside the class. Use `private set` or `init`.

```csharp
// Good
public class Job : BaseEntity<Guid>
{
    public JobStatus Status { get; private set; }

    public void Transition(JobStatus next)
    {
        if (!IsValidTransition(Status, next))
            throw new DomainException($"Cannot transition from {Status} to {next}");
        Status = next;
    }
}

// Bad — caller manipulates state directly
job.Status = JobStatus.Completed;
```

### Value Objects
- Value objects are immutable records. Use C# `record` types.
- Equality is by value, not by reference.

```csharp
public record TriggerCondition(
    ConditionOperator Operator,
    IReadOnlyList<TriggerConditionNode> Nodes
);
```

### Aggregates
- `Automation` is an aggregate root — it owns `Trigger` objects. Always load and save the full aggregate.
- `Job` is a standalone aggregate. It does not directly reference `Automation`; it holds an `AutomationId`.

### Domain Exceptions
- Throw domain-specific exceptions from within entities and value objects.
- All domain exceptions inherit from `DomainException`.
- Services translate domain exceptions into appropriate HTTP responses in `ExceptionHandlingMiddleware`.

```csharp
// FlowForge.Domain/Exceptions/DomainException.cs
public abstract class DomainException(string message) : Exception(message);

// Usage in entity
throw new InvalidJobTransitionException(Status, next);
```

---

## Project Layer Rules

### FlowForge.Domain
- **Zero NuGet dependencies.**
- Contains: Entities, Value Objects, Enums, Domain Exceptions, Domain Interfaces (e.g. `IJobRepository` — interface only, no implementation).
- Does **not** know about EF Core, Redis, HTTP, or any infrastructure.

### FlowForge.Contracts
- **Zero NuGet dependencies.**
- Contains only plain C# record types representing Redis Stream event payloads.
- No methods, no logic, no domain types.
- Naming convention: `{Subject}{Verb}Event` — e.g. `JobCreatedEvent`, `JobStatusChangedEvent`.

```csharp
// Good
public record JobCreatedEvent(
    Guid JobId,
    Guid AutomationId,
    Guid HostGroupId,
    DateTimeOffset CreatedAt
);
```

### FlowForge.Infrastructure
- Implements interfaces defined in `FlowForge.Domain`.
- All implementations are registered via `AddInfrastructure(IConfiguration config)` extension method.
- EF Core configurations go in `Configurations/` using Fluent API — never DataAnnotations on domain entities.
- Never reference ASP.NET Core types here (no `IHttpContextAccessor`, etc.).

### Services (WebApi, JobAutomator, etc.)
- Entry point logic only — wire up DI, middleware, hosted services.
- Business logic that involves multiple domain entities goes into a **service class** or **use case** inside the service project, not in controllers or workers directly.
- Controllers are thin: validate input → call service → return response.

---

## Generic Patterns for Shared Concerns

### Message Publisher

All Redis Stream publishing goes through `IMessagePublisher<TEvent>`. Never call `StackExchange.Redis` directly from service code.

```csharp
// FlowForge.Infrastructure/Messaging/Abstractions/IMessagePublisher.cs
public interface IMessagePublisher
{
    Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : class;
}
```

### Message Consumer

All Redis Stream consuming goes through `IMessageConsumer`. Implement `BackgroundService` per consumer, not per message type.

```csharp
public interface IMessageConsumer
{
    IAsyncEnumerable<TEvent> ConsumeAsync<TEvent>(
        string streamName,
        string consumerGroup,
        string consumerName,
        CancellationToken ct)
        where TEvent : class;
}
```

### Repository Pattern

Repositories are defined in `FlowForge.Domain` as interfaces and implemented in `FlowForge.Infrastructure`.

```csharp
// Domain — interface only
public interface IJobRepository
{
    Task<Job?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Job>> GetPendingAsync(CancellationToken ct = default);
    Task SaveAsync(Job job, CancellationToken ct = default);
}
```

### Multi-Database Pattern (Keyed Services)

Each `HostGroup` has a `ConnectionId` string (e.g. `wf-jobs-minion`) that maps to a dedicated database. `IJobRepository` is registered once per `ConnectionId` using **.NET Keyed Services** — no Service Locator or manual `IServiceProvider` resolution in business code.

**Registration** (in `AddInfrastructure`):

```csharp
// Read all job connection configs
var jobConnections = config.GetSection("JobConnections").Get<Dictionary<string, JobConnectionConfig>>()!;

foreach (var (connectionId, connConfig) in jobConnections)
{
    // Build a DbContext options for this connection
    var optionsBuilder = new DbContextOptionsBuilder<JobsDbContext>();
    optionsBuilder.UseNpgsql(connConfig.ConnectionString);  // or UseSqlServer, etc.
    var options = optionsBuilder.Options;

    // Register keyed — one IJobRepository per connectionId
    services.AddKeyedScoped<IJobRepository>(
        connectionId,
        (_, _) => new JobRepository(new JobsDbContext(options)));
}
```

**Dynamic resolution** (controllers, workers — where connectionId comes from the request/event):

```csharp
// JobsController
[HttpGet("{connectionId}/jobs/{id:guid}")]
public async Task<IActionResult> GetById(string connectionId, Guid id, CancellationToken ct)
{
    var repo = _serviceProvider.GetRequiredKeyedService<IJobRepository>(connectionId);
    var job  = await repo.GetByIdAsync(id, ct) ?? throw new JobNotFoundException(id);
    return Ok(JobResponse.From(job));
}
```

**Static injection** (when connectionId is known at DI registration time — e.g. a worker dedicated to one group):

```csharp
// No need for dynamic resolution — just inject the keyed service directly
builder.Services.AddScoped<IJobService>(sp =>
    new JobService(sp.GetRequiredKeyedService<IJobRepository>("wf-jobs-minion")));
```

**`ConnectionId` propagation rule**: `ConnectionId` must be carried in all Redis Stream events that relate to a Job (`JobCreatedEvent`, `JobStatusChangedEvent`). This allows the consumer on the other end to resolve the correct repository without hitting the platform DB for a lookup.

**Adding a new host group** requires only:
1. A new entry in `appsettings.json` under `JobConnections`
2. Running EF Core migrations against the new database
3. No code changes

### Redis Service (Generic Cache + Heartbeat)

```csharp
public interface IRedisService
{
    Task SetAsync(string key, string value, TimeSpan? expiry = null);
    Task<string?> GetAsync(string key);
    Task DeleteAsync(string key);
    Task RefreshHeartbeatAsync(Guid jobId, TimeSpan ttl);
    Task<bool> IsHeartbeatAliveAsync(Guid jobId);
}
```

### Transactional Event Publishing (Outbox Pattern)

> **Planned — see ROADMAP.md #4.** Currently, services call `IMessagePublisher.PublishAsync` directly after saving to the database. These are two separate I/O operations with no atomicity guarantee: a process crash between them leaves the DB updated but the event undelivered, causing permanent cache staleness in dependent services.
>
> **Planned convention:** For events that must be delivered reliably alongside a DB mutation (e.g. `AutomationChangedEvent`, `JobCreatedEvent`), use an **Outbox Writer** instead of calling `IMessagePublisher` directly from the service layer:
>
> ```csharp
> // Instead of: await publisher.PublishAsync(new AutomationChangedEvent(...), ct: ct);
> await outboxWriter.WriteAsync(new AutomationChangedEvent(...), ct);
> ```
>
> The `OutboxWriter` writes a serialized `OutboxMessage` row to the same `PlatformDbContext` transaction. A separate `OutboxRelayWorker` polls `OutboxMessages WHERE SentAt IS NULL`, publishes each to Redis, then marks `SentAt`. Consumers must tolerate duplicate delivery (they already do via Redis consumer group `XACK`).
>
> Direct `IMessagePublisher` calls remain valid for fire-and-forget cases where delivery is best-effort (e.g. heartbeats, diagnostics).

### Redis Consumer Group Bootstrap

> **Planned — see ROADMAP.md #1.** Every service that consumes a Redis Stream must ensure its consumer group exists before it begins consuming. Redis requires `XGROUP CREATE {stream} {group} $ MKSTREAM` to be called once; subsequent calls with an existing group return a `BUSYGROUP` error which should be swallowed.
>
> **Convention:** Each service's `Program.cs` calls `IStreamBootstrapper.EnsureAsync(streamName, groupName)` for every stream+group it reads, during startup, before any `BackgroundService` begins consuming. Use `$` as the starting offset — **not `0`** — to avoid re-delivering historical messages on every restart.

### Stream Name Constants

All stream names are constants in `StreamNames.cs` — never inline magic strings.

```csharp
public static class StreamNames
{
    public const string AutomationTriggered  = "flowforge:automation-triggered";
    public const string JobCreated           = "flowforge:job-created";
    public const string JobAssigned          = "flowforge:job-assigned";
    public const string JobStatusChanged     = "flowforge:job-status-changed";
    public const string JobCancelRequested   = "flowforge:job-cancel-requested";

    // Per-host stream: call HostStream("host-id") → "flowforge:host:host-id"
    public static string HostStream(string hostId) => $"flowforge:host:{hostId}";
}
```

---

### Observability (OpenTelemetry)

> **Planned — see ROADMAP.md #5.** Each service will have an `ActivitySource` named `FlowForge.{ServiceName}` (e.g. `FlowForge.WebApi`, `FlowForge.JobAutomator`). Trace context is propagated through Redis Stream messages as an extra `traceparent` field injected by `RedisStreamPublisher` and extracted by `RedisStreamConsumer`.
>
> **Span naming conventions:**
> - `publish {StreamName}` — outgoing stream messages
> - `consume {StreamName}` — incoming stream messages
> - `evaluate automation {AutomationId}` — in `AutomationWorker`
> - `dispatch job {JobId}` — in `JobDispatcherWorker`
> - `execute job {JobId}` — in `WorkflowEngine`
>
> The OpenTelemetry `TraceId` serves as the correlation ID across all services — no separate correlation field is needed on events.

---

## Coding Conventions

### Naming
| Element | Convention | Example |
|---|---|---|
| Classes / Interfaces | PascalCase | `JobDispatcherWorker`, `ILoadBalancer` |
| Methods | PascalCase | `EvaluateAsync`, `TransitionStatus` |
| Private fields | `_camelCase` | `_logger`, `_redisService` |
| Constants | PascalCase | `StreamNames.JobCreated` |
| Local variables | camelCase | `pendingJobs`, `hostId` |
| Async methods | suffix `Async` | `GetByIdAsync`, `PublishAsync` |

### Async
- All I/O methods must be async and return `Task` or `ValueTask`.
- Always pass `CancellationToken` through and name it `ct` in method signatures.
- Never use `.Result` or `.Wait()` — always `await`.

```csharp
// Good
public async Task<Job?> GetByIdAsync(Guid id, CancellationToken ct = default)
    => await _dbContext.Jobs.FindAsync([id], ct);

// Bad
public Job? GetById(Guid id)
    => _dbContext.Jobs.Find(id);
```

### Nullable Reference Types
- `<Nullable>enable</Nullable>` is on for all projects.
- Use `?` only where null is a valid, intentional return (e.g. `GetByIdAsync` returning `null` when not found).
- Never suppress with `!` unless you can prove the value is non-null at that point.

### Error Handling
- **Domain errors** → throw `DomainException` subtypes from within entities.
- **Infrastructure errors** (DB unavailable, Redis timeout) → let exceptions bubble up; `ExceptionHandlingMiddleware` maps them to HTTP 500.
- **Not Found** → return `null` from repositories, check in the service layer, throw `NotFoundException` which maps to HTTP 404.
- **Validation errors** → use `FluentValidation` in the Web API request pipeline before reaching the service layer.

### Dependency Injection
- Register all dependencies in `Program.cs` or extension methods (`AddInfrastructure`, `AddApplicationServices`).
- Never use `ServiceLocator` / `IServiceProvider` to resolve services manually at runtime — always inject via constructor.
- Prefer `IOptions<TOptions>` for configuration over reading `IConfiguration` directly in service classes.

### Worker Services
- All background workers inherit from `BackgroundService`.
- Wrap the `ExecuteAsync` loop body in try/catch — a single failed iteration should log and continue, not crash the service.
- Respect `stoppingToken` everywhere.

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    while (!stoppingToken.IsCancellationRequested)
    {
        try
        {
            await DoWorkAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Worker iteration failed, retrying...");
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
```

### Logging
- Use `ILogger<T>` injected via constructor. Never use a static logger.
- Use structured logging with named properties, not string interpolation.

```csharp
// Good
_logger.LogInformation("Job {JobId} transitioned to {Status}", job.Id, job.Status);

// Bad
_logger.LogInformation($"Job {job.Id} transitioned to {job.Status}");
```

### Configuration
- All config sections follow `PascalCase` matching the options class name.
- Every service has its own `appsettings.json` with only the settings it needs.

```csharp
public sealed class RedisOptions
{
    public const string SectionName = "Redis";
    public required string ConnectionString { get; init; }
    public TimeSpan HeartbeatTtl { get; init; } = TimeSpan.FromSeconds(30);
}
```

---

## Testing Conventions

- Test projects are named `{ProjectName}.Tests`.
- Use **xUnit** as the test framework.
- Use **Moq** or **NSubstitute** for mocking.
- Test class names: `{ClassUnderTest}Tests`.
- Test method names: `{MethodName}_{Scenario}_{ExpectedResult}`.

```csharp
public class RoundRobinLoadBalancerTests
{
    [Fact]
    public void SelectHost_WithMultipleHosts_CyclesThroughAll()
    { ... }

    [Fact]
    public void SelectHost_WithNoHosts_ThrowsNoAvailableHostException()
    { ... }
}
```

- Unit tests for pure domain logic do not need any DI setup.
- Integration tests that touch DB or Redis use `Testcontainers` to spin up real instances.

---

## Git Conventions

- Branch naming: `feature/{short-description}`, `fix/{short-description}`
- Commit messages: imperative present tense — `Add round-robin load balancer`, `Fix heartbeat TTL calculation`
- One logical change per commit.