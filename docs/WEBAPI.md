# WEBAPI.md — Web API Service

## Responsibility

The Web API is an **ASP.NET Core 10** application that serves as the central entry point for all user-facing interactions. Its responsibilities are:

1. Expose REST endpoints for managing Automations, Jobs, Host Groups, and Triggers
2. Consume `AutomationTriggeredEvent` from Redis Streams → create Jobs in the database → publish `JobCreatedEvent`
3. Consume `JobStatusChangedEvent` from Redis Streams → update Job status in the database → push real-time updates to the frontend via SignalR
4. Handle cancel and remove requests for Jobs
5. Expose webhook endpoints that allow external systems to fire Automation triggers

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
| `POST` | `/api/automations/{id}/webhook` | Fire webhook trigger for automation |

### Jobs

| Method | Route | Description |
|---|---|---|
| `GET` | `/api/jobs` | List jobs (filterable by status, automationId) |
| `GET` | `/api/jobs/{id}` | Get single job |
| `POST` | `/api/jobs/{id}/cancel` | Request cancellation of a running job |
| `DELETE` | `/api/jobs/{id}` | Remove a pending job (status → Removed) |

### Host Groups

| Method | Route | Description |
|---|---|---|
| `GET` | `/api/host-groups` | List all host groups |
| `GET` | `/api/host-groups/{id}` | Get host group with its online hosts |
| `POST` | `/api/host-groups` | Create host group |
| `DELETE` | `/api/host-groups/{id}` | Delete host group |

### Hosts

| Method | Route | Description |
|---|---|---|
| `GET` | `/api/hosts` | List all registered hosts |
| `GET` | `/api/hosts/{id}` | Get single host with status |

### Triggers

| Method | Route | Description |
|---|---|---|
| `GET` | `/api/triggers/types` | List available trigger types with their config schemas |

---

## Controllers

### AutomationsController

```csharp
[ApiController]
[Route("api/automations")]
public class AutomationsController(IAutomationService automationService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] AutomationQueryParams query, CancellationToken ct)
    {
        var result = await automationService.GetAllAsync(query, ct);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var result = await automationService.GetByIdAsync(id, ct);
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAutomationRequest request, CancellationToken ct)
    {
        var created = await automationService.CreateAsync(request, ct);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAutomationRequest request, CancellationToken ct)
    {
        var updated = await automationService.UpdateAsync(id, request, ct);
        return Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await automationService.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/webhook")]
    public async Task<IActionResult> FireWebhook(Guid id, [FromHeader(Name = "X-Webhook-Secret")] string? secret, CancellationToken ct)
    {
        await automationService.FireWebhookAsync(id, secret, ct);
        return Accepted();
    }
}
```

### JobsController

```csharp
[ApiController]
[Route("api/jobs")]
public class JobsController(IJobService jobService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] JobQueryParams query, CancellationToken ct)
        => Ok(await jobService.GetAllAsync(query, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
        => Ok(await jobService.GetByIdAsync(id, ct));

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        await jobService.RequestCancelAsync(id, ct);
        return Accepted();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        await jobService.RemoveAsync(id, ct);
        return NoContent();
    }
}
```

---

## Service Layer

Controllers are thin — business logic lives in service classes.

### IAutomationService

```csharp
public interface IAutomationService
{
    Task<PagedResult<AutomationResponse>> GetAllAsync(AutomationQueryParams query, CancellationToken ct);
    Task<AutomationResponse> GetByIdAsync(Guid id, CancellationToken ct);
    Task<AutomationResponse> CreateAsync(CreateAutomationRequest request, CancellationToken ct);
    Task<AutomationResponse> UpdateAsync(Guid id, UpdateAutomationRequest request, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
    Task FireWebhookAsync(Guid id, string? secret, CancellationToken ct);
}
```

### IJobService

```csharp
public interface IJobService
{
    Task<PagedResult<JobResponse>> GetAllAsync(JobQueryParams query, CancellationToken ct);
    Task<JobResponse> GetByIdAsync(Guid id, CancellationToken ct);
    Task RequestCancelAsync(Guid id, CancellationToken ct);
    Task RemoveAsync(Guid id, CancellationToken ct);
}
```

### Cancel vs Remove logic

```csharp
// JobService.RequestCancelAsync
public async Task RequestCancelAsync(Guid id, CancellationToken ct)
{
    var job = await _jobRepo.GetByIdAsync(id, ct) ?? throw new JobNotFoundException(id);

    if (job.Status == JobStatus.Pending)
    {
        // Not yet dispatched — just mark as Removed, no need to signal host
        job.Transition(JobStatus.Removed);
        await _jobRepo.SaveAsync(job, ct);
        return;
    }

    if (!job.Status.IsCancellable())
        throw new InvalidJobTransitionException(job.Status, JobStatus.Cancel);

    // Signal the running host to kill the process
    job.Transition(JobStatus.Cancel);
    await _jobRepo.SaveAsync(job, ct);

    await _publisher.PublishAsync(new JobCancelRequestedEvent(
        JobId:       job.Id,
        HostId:      job.HostId!.Value,
        RequestedAt: DateTimeOffset.UtcNow
    ), ct);
}

// JobService.RemoveAsync — only valid for Pending jobs
public async Task RemoveAsync(Guid id, CancellationToken ct)
{
    var job = await _jobRepo.GetByIdAsync(id, ct) ?? throw new JobNotFoundException(id);

    if (job.Status != JobStatus.Pending)
        throw new DomainException("Only pending jobs can be removed via DELETE. Use POST /cancel for running jobs.");

    job.Transition(JobStatus.Removed);
    await _jobRepo.SaveAsync(job, ct);
}
```

---

## Background Workers

The Web API hosts two background workers that consume Redis Streams.

### AutomationTriggeredConsumer

Listens for `AutomationTriggeredEvent`, creates a `Job` in the database, and publishes `JobCreatedEvent`.

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
        await foreach (var @event in consumer.ConsumeAsync<AutomationTriggeredEvent>(
            StreamNames.AutomationTriggered, "webapi", "webapi-1", stoppingToken))
        {
            try
            {
                var automation = await automationRepo.GetByIdAsync(@event.AutomationId, stoppingToken)
                    ?? throw new AutomationNotFoundException(@event.AutomationId);

                var job = Job.Create(
                    automationId: automation.Id,
                    hostGroupId:  automation.HostGroupId,
                    triggeredAt:  @event.TriggeredAt);

                await jobRepo.SaveAsync(job, stoppingToken);

                await publisher.PublishAsync(new JobCreatedEvent(
                    JobId:       job.Id,
                    AutomationId: job.AutomationId,
                    HostGroupId:  job.HostGroupId,
                    CreatedAt:    job.CreatedAt
                ), stoppingToken);

                logger.LogInformation(
                    "Job {JobId} created for automation {AutomationId}",
                    job.Id, automation.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to process AutomationTriggeredEvent for automation {AutomationId}",
                    @event.AutomationId);
            }
        }
    }
}
```

### JobStatusChangedConsumer

Listens for `JobStatusChangedEvent`, updates Job status in the database, and pushes the update to the frontend via SignalR.

```csharp
public class JobStatusChangedConsumer(
    IMessageConsumer consumer,
    IJobRepository jobRepo,
    IHubContext<JobStatusHub, IJobStatusClient> hubContext,
    ILogger<JobStatusChangedConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var @event in consumer.ConsumeAsync<JobStatusChangedEvent>(
            StreamNames.JobStatusChanged, "webapi", "webapi-1", stoppingToken))
        {
            try
            {
                var job = await jobRepo.GetByIdAsync(@event.JobId, stoppingToken);
                if (job is null)
                {
                    logger.LogWarning("Received status change for unknown job {JobId}", @event.JobId);
                    continue;
                }

                job.Transition(@event.Status);
                if (@event.Message is not null) job.SetMessage(@event.Message);
                await jobRepo.SaveAsync(job, stoppingToken);

                // Push to all frontend clients subscribed to this job
                await hubContext.Clients
                    .Group($"job:{job.Id}")
                    .OnJobStatusChanged(new JobStatusUpdate(
                        JobId:     job.Id,
                        Status:    job.Status,
                        Message:   job.Message,
                        UpdatedAt: @event.UpdatedAt));
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to process JobStatusChangedEvent for job {JobId}", @event.JobId);
            }
        }
    }
}
```

---

## SignalR Hub

SignalR is used **exclusively for frontend real-time updates**. Service-to-service communication never goes through SignalR.

```csharp
// Hub interface (strongly typed)
public interface IJobStatusClient
{
    Task OnJobStatusChanged(JobStatusUpdate update);
}

public class JobStatusHub : Hub<IJobStatusClient>
{
    // Frontend subscribes to updates for a specific job
    public async Task SubscribeToJob(Guid jobId)
        => await Groups.AddToGroupAsync(Context.ConnectionId, $"job:{jobId}");

    public async Task UnsubscribeFromJob(Guid jobId)
        => await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"job:{jobId}");
}
```

Frontend connects to `/hubs/job-status` and calls `SubscribeToJob(jobId)` after loading a job detail view.

---

## DTOs

### CreateAutomationRequest

```csharp
public record CreateAutomationRequest(
    string                   Name,
    string?                  Description,
    Guid                     HostGroupId,
    List<CreateTriggerRequest>    Triggers,
    TriggerConditionRequest  TriggerCondition
);

public record CreateTriggerRequest(
    TriggerType TriggerType,
    string      ConfigJson      // serialized trigger-type-specific config
);

public record TriggerConditionRequest(
    ConditionOperator?               Operator,   // null if single trigger (leaf node)
    Guid?                            TriggerId,  // set if leaf node
    List<TriggerConditionRequest>?   Nodes       // set if composite node
);
```

### AutomationResponse

```csharp
public record AutomationResponse(
    Guid                         Id,
    string                       Name,
    string?                      Description,
    Guid                         HostGroupId,
    List<TriggerResponse>        Triggers,
    TriggerConditionResponse     TriggerCondition,
    DateTimeOffset               CreatedAt,
    DateTimeOffset               UpdatedAt
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

### JobStatusUpdate (SignalR payload)

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

Use `FluentValidation` for all request DTOs. Validators are registered automatically and run as part of the request pipeline before reaching the controller.

```csharp
public class CreateAutomationRequestValidator : AbstractValidator<CreateAutomationRequest>
{
    public CreateAutomationRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(x => x.HostGroupId)
            .NotEmpty();

        RuleFor(x => x.Triggers)
            .NotEmpty()
            .WithMessage("At least one trigger is required");

        RuleFor(x => x.TriggerCondition)
            .NotNull();
    }
}
```

Register in `Program.cs`:

```csharp
builder.Services
    .AddFluentValidationAutoValidation()
    .AddValidatorsFromAssemblyContaining<CreateAutomationRequestValidator>();
```

---

## Exception Handling Middleware

Maps domain and infrastructure exceptions to appropriate HTTP responses. Registered as the first middleware in the pipeline.

```csharp
public class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception ex)
    {
        var (statusCode, title) = ex switch
        {
            JobNotFoundException or AutomationNotFoundException
                => (StatusCodes.Status404NotFound, "Resource not found"),

            InvalidJobTransitionException or DomainException
                => (StatusCodes.Status422UnprocessableEntity, "Business rule violation"),

            ValidationException
                => (StatusCodes.Status400BadRequest, "Validation failed"),

            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred")
        };

        if (statusCode == StatusCodes.Status500InternalServerError)
            logger.LogError(ex, "Unhandled exception");

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

When an external system fires the webhook endpoint:

```
POST /api/automations/{id}/webhook
Header: X-Webhook-Secret: <secret>

1. AutomationService validates the secret against the stored hash
2. Sets Redis flag: trigger:webhook:{triggerId}:fired = 1 (TTL 10 min)
3. Returns 202 Accepted immediately
4. JobAutomator reads the flag on its next evaluation pass
```

```csharp
public async Task FireWebhookAsync(Guid automationId, string? secret, CancellationToken ct)
{
    var automation = await _automationRepo.GetByIdAsync(automationId, ct)
        ?? throw new AutomationNotFoundException(automationId);

    var webhookTrigger = automation.Triggers.FirstOrDefault(t => t.Type == TriggerType.Webhook)
        ?? throw new DomainException("Automation does not have a webhook trigger");

    var config = JsonSerializer.Deserialize<WebhookTriggerConfig>(webhookTrigger.ConfigJson)!;

    if (config.SecretHash is not null)
    {
        if (secret is null || !VerifySecret(secret, config.SecretHash))
            throw new UnauthorizedWebhookException(automationId);
    }

    await _redis.SetAsync(
        key:    $"trigger:webhook:{webhookTrigger.Id}:fired",
        value:  "1",
        expiry: TimeSpan.FromMinutes(10));
}
```

---

## Configuration

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=postgres;Database=flowforge;Username=flowforge;Password=..."
  },
  "Redis": {
    "ConnectionString": "redis:6379"
  },
  "SignalR": {
    "HubPath": "/hubs/job-status"
  },
  "AllowedOrigins": [
    "http://localhost:3000"
  ]
}
```

---

## DI Registration (Program.cs sketch)

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddInfrastructure(builder.Configuration)
    .AddControllers();

builder.Services
    .AddFluentValidationAutoValidation()
    .AddValidatorsFromAssemblyContaining<CreateAutomationRequestValidator>();

builder.Services
    .AddScoped<IAutomationService, AutomationService>()
    .AddScoped<IJobService, JobService>();

builder.Services
    .AddSignalR();

builder.Services
    .AddHostedService<AutomationTriggeredConsumer>()
    .AddHostedService<JobStatusChangedConsumer>();

builder.Services
    .AddCors(options => options.AddDefaultPolicy(policy =>
        policy.WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>()!)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()));   // required for SignalR

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseCors();
app.MapControllers();
app.MapHub<JobStatusHub>("/hubs/job-status");

app.Run();
```