# WEBAPI.md — Web API Service

## Responsibility

The Web API is an **ASP.NET Core 10** application. Its responsibilities are:

1. Expose REST endpoints for Automations, Jobs, Host Groups, Triggers
2. Consume `AutomationTriggeredEvent` → create Jobs → publish `JobCreatedEvent`
3. Consume `JobStatusChangedEvent` → update Job status → push SignalR updates to frontend
4. Handle cancel/remove requests for Jobs
5. Expose webhook endpoints for external systems to fire Automation triggers

---

## Endpoints Overview

### Automations

| Method | Route | Description |
|---|---|---|
| `GET` | `/api/automations` | List all automations (paginated) |
| `GET` | `/api/automations/{id}` | Get single automation |
| `POST` | `/api/automations` | Create automation |
| `PUT` | `/api/automations/{id}` | Update automation |
| `DELETE` | `/api/automations/{id}` | Delete automation |
| `PUT` | `/api/automations/{id}/enable` | Enable automation |
| `PUT` | `/api/automations/{id}/disable` | Disable automation |
| `POST` | `/api/automations/{id}/webhook` | Fire webhook trigger |

### Jobs

| Method | Route | Description |
|---|---|---|
| `GET` | `/api/{connectionId}/jobs` | List jobs (filterable) |
| `GET` | `/api/{connectionId}/jobs/{id}` | Get single job |
| `POST` | `/api/{connectionId}/jobs/{id}/cancel` | Request cancellation |
| `DELETE` | `/api/{connectionId}/jobs/{id}` | Remove a pending job |

### Triggers

| Method | Route | Description |
|---|---|---|
| `GET` | `/api/triggers/types` | List all trigger types with config schemas |
| `GET` | `/api/triggers/types/{typeId}` | Get schema for one type |
| `POST` | `/api/triggers/types/{typeId}/validate-config` | Validate a configJson before saving |

See **TRIGGERS.md** for the full `TriggersController` implementation, all built-in descriptors, and the `custom-script` type.

### Host Groups

| Method | Route | Description |
|---|---|---|
| `GET` | `/api/host-groups` | List host groups |
| `GET` | `/api/host-groups/{id}` | Get host group + online hosts |
| `POST` | `/api/host-groups` | Create host group |
| `DELETE` | `/api/host-groups/{id}` | Delete host group |

### Hosts

| Method | Route | Description |
|---|---|---|
| `GET` | `/api/hosts` | List all hosts |
| `GET` | `/api/hosts/{id}` | Get single host |

---

## Controllers

### AutomationsController

```csharp
[ApiController]
[Route("api/automations")]
public class AutomationsController(
    IAutomationService automationService,
    ILogger<AutomationsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] AutomationQueryParams query, CancellationToken ct)
        => Ok(await automationService.GetAllAsync(query, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
        => Ok(await automationService.GetByIdAsync(id, ct));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAutomationRequest request, CancellationToken ct)
    {
        var created = await automationService.CreateAsync(request, ct);
        logger.LogInformation("Automation {AutomationId} ({Name}) created", created.Id, created.Name);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAutomationRequest request, CancellationToken ct)
    {
        var updated = await automationService.UpdateAsync(id, request, ct);
        logger.LogInformation("Automation {AutomationId} updated", id);
        return Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await automationService.DeleteAsync(id, ct);
        logger.LogInformation("Automation {AutomationId} deleted", id);
        return NoContent();
    }

    [HttpPut("{id:guid}/enable")]
    public async Task<IActionResult> Enable(Guid id, CancellationToken ct)
    {
        await automationService.EnableAsync(id, ct);
        logger.LogInformation("Automation {AutomationId} enabled", id);
        return NoContent();
    }

    [HttpPut("{id:guid}/disable")]
    public async Task<IActionResult> Disable(Guid id, CancellationToken ct)
    {
        await automationService.DisableAsync(id, ct);
        logger.LogInformation("Automation {AutomationId} disabled", id);
        return NoContent();
    }

    [HttpPost("{id:guid}/webhook")]
    public async Task<IActionResult> FireWebhook(
        Guid id,
        [FromHeader(Name = "X-Webhook-Secret")] string? secret,
        CancellationToken ct)
    {
        await automationService.FireWebhookAsync(id, secret, ct);
        return Accepted();
    }
}
```

### JobsController

```csharp
[ApiController]
[Route("api/{connectionId}/jobs")]
public class JobsController(
    IServiceProvider serviceProvider,
    IJobService jobService,
    ILogger<JobsController> logger) : ControllerBase
{
    private IJobRepository GetRepo(string connectionId)
        => serviceProvider.GetRequiredKeyedService<IJobRepository>(connectionId);

    [HttpGet]
    public async Task<IActionResult> GetAll(string connectionId, [FromQuery] JobQueryParams query, CancellationToken ct)
        => Ok(await jobService.GetAllAsync(GetRepo(connectionId), query, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(string connectionId, Guid id, CancellationToken ct)
    {
        var job = await GetRepo(connectionId).GetByIdAsync(id, ct) ?? throw new JobNotFoundException(id);
        return Ok(JobResponse.From(job));
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(string connectionId, Guid id, CancellationToken ct)
    {
        await jobService.RequestCancelAsync(GetRepo(connectionId), id, ct);
        logger.LogInformation("Cancel requested for job {JobId} (connectionId={ConnectionId})", id, connectionId);
        return Accepted();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(string connectionId, Guid id, CancellationToken ct)
    {
        await jobService.RemoveAsync(GetRepo(connectionId), id, ct);
        logger.LogInformation("Job {JobId} removed (connectionId={ConnectionId})", id, connectionId);
        return NoContent();
    }
}
```

> `GetRequiredKeyedService` throws `InvalidOperationException` for unregistered `connectionId`. `ExceptionHandlingMiddleware` maps this to HTTP 400.

### TriggersController

See **TRIGGERS.md** for the full implementation. Summary:

```csharp
[ApiController]
[Route("api/triggers")]
public class TriggersController(ITriggerTypeRegistry registry, ILogger<TriggersController> logger)
    : ControllerBase
{
    [HttpGet("types")]                          // list all types + schemas
    [HttpGet("types/{typeId}")]                 // schema for one type
    [HttpPost("types/{typeId}/validate-config")] // validate configJson before saving
}
```

---

## Service Layer

### IAutomationService

```csharp
public interface IAutomationService
{
    Task<PagedResult<AutomationResponse>> GetAllAsync(AutomationQueryParams query, CancellationToken ct);
    Task<AutomationResponse> GetByIdAsync(Guid id, CancellationToken ct);
    Task<AutomationResponse> CreateAsync(CreateAutomationRequest request, CancellationToken ct);
    Task<AutomationResponse> UpdateAsync(Guid id, UpdateAutomationRequest request, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
    Task EnableAsync(Guid id, CancellationToken ct);
    Task DisableAsync(Guid id, CancellationToken ct);
    Task FireWebhookAsync(Guid id, string? secret, CancellationToken ct);
    Task<IReadOnlyList<AutomationSnapshot>> GetAllSnapshotsAsync(CancellationToken ct);
}
```

`AutomationService.CreateAsync` calls `_registry.ValidateTriggerConfigs(request.Triggers)` before constructing the domain entity — see **TRIGGERS.md** for the validation helper. Every mutation publishes `AutomationChangedEvent` so `JobAutomator` keeps its cache current.

```csharp
// Example: AutomationService.CreateAsync (abbreviated)
public async Task<AutomationResponse> CreateAsync(CreateAutomationRequest request, CancellationToken ct)
{
    _logger.LogInformation("Creating automation '{Name}' with {TriggerCount} trigger(s)",
        request.Name, request.Triggers.Count);

    // 1. Validate each trigger's TypeId is known and configJson is valid
    ValidateTriggerConfigs(request.Triggers);

    // 2. Build domain entity (throws on invariant violations)
    var automation = Automation.Create(
        name:          request.Name,
        description:   request.Description,
        hostGroupId:   request.HostGroupId,
        taskId:        request.TaskId,
        triggers:      request.Triggers
                           .Select(t => Trigger.Create(t.Name, t.TypeId, t.ConfigJson))
                           .ToList(),
        conditionRoot: MapConditionNode(request.TriggerCondition));

    await _automationRepo.SaveAsync(automation, ct);

    await _publisher.PublishAsync(new AutomationChangedEvent(
        AutomationId: automation.Id,
        ChangeType:   ChangeType.Created,
        Automation:   AutomationSnapshot.From(automation)
    ), ct);

    _logger.LogInformation("Automation {AutomationId} saved and cache notified", automation.Id);
    return AutomationResponse.From(automation);
}

private void ValidateTriggerConfigs(IEnumerable<CreateTriggerRequest> triggers)
{
    foreach (var t in triggers)
    {
        if (!_registry.IsKnown(t.TypeId))
            throw new InvalidAutomationException(
                $"Unknown trigger type '{t.TypeId}'. Call GET /api/triggers/types to see available types.");

        var errors = _registry.Get(t.TypeId)!.ValidateConfig(t.ConfigJson);
        if (errors.Count > 0)
            throw new InvalidAutomationException(
                $"Trigger '{t.Name}' (type '{t.TypeId}') has invalid config: {string.Join("; ", errors)}");
    }
}
```

### IJobService

```csharp
public interface IJobService
{
    Task<PagedResult<JobResponse>> GetAllAsync(IJobRepository repo, JobQueryParams query, CancellationToken ct);
    Task<JobResponse> GetByIdAsync(IJobRepository repo, Guid id, CancellationToken ct);
    Task RequestCancelAsync(IJobRepository repo, Guid id, CancellationToken ct);
    Task RemoveAsync(IJobRepository repo, Guid id, CancellationToken ct);
}
```

```csharp
// Cancel logic
public async Task RequestCancelAsync(IJobRepository repo, Guid id, CancellationToken ct)
{
    var job = await repo.GetByIdAsync(id, ct) ?? throw new JobNotFoundException(id);

    if (job.Status == JobStatus.Pending)
    {
        _logger.LogInformation("Job {JobId} is Pending — removing without signalling host", id);
        job.Transition(JobStatus.Removed);
        await repo.SaveAsync(job, ct);
        return;
    }

    if (!job.Status.IsCancellable())
        throw new InvalidJobTransitionException(job.Status, JobStatus.Cancel);

    job.Transition(JobStatus.Cancel);
    await repo.SaveAsync(job, ct);

    await _publisher.PublishAsync(new JobCancelRequestedEvent(
        JobId:       job.Id,
        HostId:      job.HostId!.Value,
        RequestedAt: DateTimeOffset.UtcNow
    ), ct);

    _logger.LogInformation("Job {JobId} → Cancel; signal sent to host {HostId}", job.Id, job.HostId);
}

// Remove logic (Pending only)
public async Task RemoveAsync(IJobRepository repo, Guid id, CancellationToken ct)
{
    var job = await repo.GetByIdAsync(id, ct) ?? throw new JobNotFoundException(id);
    if (job.Status != JobStatus.Pending)
        throw new DomainException("Only Pending jobs can be removed via DELETE. Use POST /cancel for running jobs.");
    job.Transition(JobStatus.Removed);
    await repo.SaveAsync(job, ct);
    _logger.LogInformation("Job {JobId} removed (was Pending)", id);
}
```

---

## Background Workers

### AutomationTriggeredConsumer

```csharp
public class AutomationTriggeredConsumer(
    IMessageConsumer consumer,
    IMessagePublisher publisher,
    IAutomationRepository automationRepo,
    IJobRepository jobRepo,
    ILogger<AutomationTriggeredConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("AutomationTriggeredConsumer started");

        await foreach (var @event in consumer.ConsumeAsync<AutomationTriggeredEvent>(
            StreamNames.AutomationTriggered, "webapi", "webapi-1", stoppingToken))
        {
            try { await HandleEventAsync(@event, stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to process AutomationTriggeredEvent for automation {AutomationId}",
                    @event.AutomationId);
            }
        }
    }

    private async Task HandleEventAsync(AutomationTriggeredEvent @event, CancellationToken ct)
    {
        // Re-check IsEnabled — automation may have been disabled between firing and now
        var automation = await automationRepo.GetByIdAsync(@event.AutomationId, ct)
            ?? throw new AutomationNotFoundException(@event.AutomationId);

        if (!automation.IsEnabled)
        {
            logger.LogInformation(
                "Automation {AutomationId} is disabled — discarding triggered event",
                @event.AutomationId);
            return;
        }

        var job = Job.Create(
            automationId: automation.Id,
            hostGroupId:  automation.HostGroupId,
            taskId:       automation.TaskId,
            connectionId: @event.ConnectionId,
            triggeredAt:  @event.TriggeredAt);

        await jobRepo.SaveAsync(job, ct);

        await publisher.PublishAsync(new JobCreatedEvent(
            JobId:        job.Id,
            AutomationId: job.AutomationId,
            HostGroupId:  job.HostGroupId,
            ConnectionId: @event.ConnectionId,
            CreatedAt:    job.CreatedAt
        ), ct);

        logger.LogInformation(
            "Job {JobId} created for automation {AutomationId} (connectionId={ConnectionId})",
            job.Id, automation.Id, @event.ConnectionId);
    }
}
```

### JobStatusChangedConsumer

```csharp
public class JobStatusChangedConsumer(
    IMessageConsumer consumer,
    IServiceProvider serviceProvider,
    IHubContext<JobStatusHub, IJobStatusClient> hubContext,
    ILogger<JobStatusChangedConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("JobStatusChangedConsumer started");

        await foreach (var @event in consumer.ConsumeAsync<JobStatusChangedEvent>(
            StreamNames.JobStatusChanged, "webapi", "webapi-1", stoppingToken))
        {
            try { await HandleEventAsync(@event, stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to process JobStatusChangedEvent for job {JobId}", @event.JobId);
            }
        }
    }

    private async Task HandleEventAsync(JobStatusChangedEvent @event, CancellationToken ct)
    {
        // Resolve correct job repo by ConnectionId carried in the event
        var repo = serviceProvider.GetRequiredKeyedService<IJobRepository>(@event.ConnectionId);
        var job  = await repo.GetByIdAsync(@event.JobId, ct);

        if (job is null)
        {
            logger.LogWarning(
                "Status change for unknown job {JobId} (connectionId={ConnectionId})",
                @event.JobId, @event.ConnectionId);
            return;
        }

        job.Transition(@event.Status);
        if (@event.Message is not null) job.SetMessage(@event.Message);
        await repo.SaveAsync(job, ct);

        logger.LogInformation("Job {JobId} → {Status}", job.Id, job.Status);

        await hubContext.Clients
            .Group($"job:{job.Id}")
            .OnJobStatusChanged(new JobStatusUpdate(
                JobId:     job.Id,
                Status:    job.Status,
                Message:   job.Message,
                UpdatedAt: @event.UpdatedAt));
    }
}
```

---

## SignalR Hub

```csharp
public interface IJobStatusClient
{
    Task OnJobStatusChanged(JobStatusUpdate update);
}

public class JobStatusHub : Hub<IJobStatusClient>
{
    public async Task SubscribeToJob(Guid jobId)
        => await Groups.AddToGroupAsync(Context.ConnectionId, $"job:{jobId}");

    public async Task UnsubscribeFromJob(Guid jobId)
        => await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"job:{jobId}");
}
```

---

## DTOs

### CreateAutomationRequest

```csharp
public record CreateAutomationRequest(
    string                        Name,
    string?                       Description,
    Guid                          HostGroupId,
    string                        TaskId,
    List<CreateTriggerRequest>    Triggers,
    TriggerConditionRequest       TriggerCondition
);

public record CreateTriggerRequest(
    string  Name,       // unique within this automation — used in condition expressions
    string  TypeId,     // e.g. "schedule", "sql", "custom-script" — call GET /api/triggers/types
    string  ConfigJson  // type-specific JSON — use POST /api/triggers/types/{typeId}/validate-config first
);

public record TriggerConditionRequest(
    ConditionOperator?                Operator,
    string?                           TriggerName,  // matches a Name in Triggers list
    List<TriggerConditionRequest>?    Nodes
);
```

**Example request:**
```json
{
  "name": "Nightly ETL after API ready",
  "hostGroupId": "00000000-0000-0000-0000-000000000001",
  "taskId": "run-script",
  "triggers": [
    {
      "name": "daily-schedule",
      "typeId": "schedule",
      "configJson": "{\"cronExpression\":\"0 0 2 * * ?\"}"
    },
    {
      "name": "api-ready-check",
      "typeId": "custom-script",
      "configJson": "{\"scriptContent\":\"import requests\\nresp = requests.get('https://api.example.com/ready')\\nprint('true' if resp.ok else 'false')\",\"pollingIntervalSeconds\":60,\"timeoutSeconds\":10}"
    }
  ],
  "triggerCondition": {
    "operator": "And",
    "nodes": [
      { "triggerName": "daily-schedule" },
      { "triggerName": "api-ready-check" }
    ]
  }
}
```

### UpdateAutomationRequest

```csharp
public record UpdateAutomationRequest(
    string                        Name,
    string?                       Description,
    Guid                          HostGroupId,
    string                        TaskId,
    List<CreateTriggerRequest>    Triggers,
    TriggerConditionRequest       TriggerCondition
);
```

> `IsEnabled` is **not** part of `UpdateAutomationRequest`. Use the dedicated `PUT /enable` and `PUT /disable` endpoints.

### AutomationResponse

```csharp
public record AutomationResponse(
    Guid                         Id,
    string                       Name,
    string?                      Description,
    Guid                         HostGroupId,
    string                       TaskId,
    bool                         IsEnabled,
    List<TriggerResponse>        Triggers,
    TriggerConditionResponse     TriggerCondition,
    DateTimeOffset               CreatedAt,
    DateTimeOffset               UpdatedAt
);

public record TriggerResponse(
    Guid    Id,
    string  Name,
    string  TypeId,     // e.g. "schedule", "custom-script"
    string  ConfigJson
);

public record TriggerConditionResponse(
    ConditionOperator?                Operator,
    string?                           TriggerName,
    List<TriggerConditionResponse>?   Nodes
);
```

### JobResponse

```csharp
public record JobResponse(
    Guid            Id,
    Guid            AutomationId,
    string          AutomationName,
    Guid            HostGroupId,
    Guid?           HostId,
    JobStatus       Status,
    string?         Message,
    DateTimeOffset  CreatedAt,
    DateTimeOffset  UpdatedAt
);
```

### JobStatusUpdate (SignalR)

```csharp
public record JobStatusUpdate(
    Guid            JobId,
    JobStatus       Status,
    string?         Message,
    DateTimeOffset  UpdatedAt
);
```

---

## Validation

```csharp
public class CreateAutomationRequestValidator : AbstractValidator<CreateAutomationRequest>
{
    public CreateAutomationRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.HostGroupId).NotEmpty();
        RuleFor(x => x.TaskId).NotEmpty();

        RuleFor(x => x.Triggers)
            .NotEmpty()
            .WithMessage("At least one trigger is required.");

        // Each trigger must have a non-empty unique name and a non-empty TypeId
        RuleForEach(x => x.Triggers).ChildRules(trigger =>
        {
            trigger.RuleFor(t => t.Name)
                .NotEmpty().MaximumLength(100)
                .WithMessage("Each trigger must have a non-empty name (max 100 chars).");
            trigger.RuleFor(t => t.TypeId)
                .NotEmpty()
                .WithMessage("Each trigger must have a non-empty TypeId. Call GET /api/triggers/types.");
        });

        // Trigger names must be unique within the automation
        RuleFor(x => x.Triggers)
            .Must(triggers =>
                triggers.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() == triggers.Count)
            .WithMessage("Trigger names must be unique within an automation.");

        // TriggerCondition is required
        RuleFor(x => x.TriggerCondition)
            .NotNull()
            .WithMessage("TriggerCondition is required. " +
                         "For a single trigger, use: { \"triggerName\": \"your-trigger-name\" }.");

        // All TriggerNames in the condition tree must match a trigger in the Triggers list
        RuleFor(x => x)
            .Must(req => req.TriggerCondition == null ||
                         AllConditionNamesExist(
                             req.TriggerCondition,
                             req.Triggers.Select(t => t.Name).ToHashSet(StringComparer.Ordinal)))
            .WithMessage("TriggerCondition references a TriggerName not present in the Triggers list.")
            .When(x => x.Triggers.Count > 0 && x.TriggerCondition is not null);
    }

    private static bool AllConditionNamesExist(TriggerConditionRequest node, HashSet<string> knownNames)
    {
        if (node.TriggerName is not null) return knownNames.Contains(node.TriggerName);
        return node.Nodes?.All(n => AllConditionNamesExist(n, knownNames)) ?? true;
    }
}
```

> `TypeId` validity and `configJson` correctness are validated in `AutomationService.ValidateTriggerConfigs` (service layer), not in the FluentValidation validator. FluentValidation runs before the service; the service provides better error messages for type-specific config errors.

---

## Exception Handling Middleware

```csharp
public class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    private async Task HandleExceptionAsync(HttpContext context, Exception ex)
    {
        var (statusCode, title) = ex switch
        {
            JobNotFoundException or AutomationNotFoundException
                => (404, "Resource not found"),

            InvalidJobTransitionException or InvalidAutomationException
            or InvalidTriggerConditionException or DomainException
                => (422, "Business rule violation"),

            InvalidOperationException when ex.Message.Contains("No service")
                => (400, "Unknown connection ID"),

            ValidationException
                => (400, "Validation failed"),

            _ => (500, "An unexpected error occurred")
        };

        if (statusCode == 500)
            logger.LogError(ex, "Unhandled exception");
        else
            logger.LogWarning(ex, "Request failed: {StatusCode} {Title}", statusCode, title);

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title  = title,
            Status = statusCode,
            Detail = ex.Message
        });
    }
}
```

---

## Webhook Flow

```
POST /api/automations/{id}/webhook   Header: X-Webhook-Secret: <secret>

1. Verify automation exists and IsEnabled (returns 422 if disabled)
2. Validate webhook secret
3. Set Redis flag: trigger:webhook:{triggerId}:fired = 1 (TTL 10 min)
4. Return 202 Accepted
5. JobAutomator reads the flag on next evaluation pass
```

---

## Configuration

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=postgres;Database=flowforge;..."
  },
  "Redis": {
    "ConnectionString": "redis:6379"
  },
  "SignalR": {
    "HubPath": "/hubs/job-status"
  },
  "AllowedOrigins": ["http://localhost:3000"]
}
```

---

## DI Registration (Program.cs sketch)

```csharp
builder.Services
    .AddInfrastructure(builder.Configuration)  // registers ITriggerTypeRegistry + all descriptors
    .AddControllers();

builder.Services
    .AddFluentValidationAutoValidation()
    .AddValidatorsFromAssemblyContaining<CreateAutomationRequestValidator>();

builder.Services
    .AddScoped<IAutomationService, AutomationService>()
    .AddScoped<IJobService, JobService>();

builder.Services.AddSignalR();

builder.Services
    .AddHostedService<AutomationTriggeredConsumer>()
    .AddHostedService<JobStatusChangedConsumer>();

builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>()!)
          .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseCors();
app.MapControllers();
app.MapHub<JobStatusHub>("/hubs/job-status");
app.Run();
```