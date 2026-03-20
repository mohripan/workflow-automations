using FlowForge.Infrastructure;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.Redis;
using OpenTelemetry.Trace;
using FlowForge.WebApi.Hubs;
using FlowForge.WebApi.Middleware;
using FlowForge.WebApi.Options;
using FlowForge.WebApi.Services;
using FlowForge.WebApi.Workers;
using FlowForge.WebApi.Validators;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.OpenApi;
using FluentValidation;
using FluentValidation.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Add Infrastructure
builder.Services.AddInfrastructure(builder.Configuration, "WebApi");
builder.Services.AddOpenTelemetry().WithTracing(t => t.AddAspNetCoreInstrumentation());

// Add Health Checks
var pgConnStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:DefaultConnection");
var redisConnStr = builder.Configuration["Redis:ConnectionString"]
    ?? throw new InvalidOperationException("Missing Redis:ConnectionString");

builder.Services.AddHealthChecks()
    .AddNpgSql(pgConnStr, healthQuery: "SELECT 1;", name: "postgres")
    .AddRedis(redisConnStr, name: "redis");

// Add Services
builder.Services.AddScoped<IAutomationService, AutomationService>();
builder.Services.AddScoped<IJobService, JobService>();

// Add Controllers
builder.Services.AddControllers();

// Add OpenAPI
builder.Services.AddOpenApi();

// Add FluentValidation
builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssemblyContaining<CreateAutomationRequestValidator>();

// Add SignalR
builder.Services.AddSignalR();

// Worker options
builder.Services.Configure<OutboxRelayOptions>(
    builder.Configuration.GetSection(OutboxRelayOptions.SectionName));

// Add Background Workers
builder.Services.AddHostedService<AutomationTriggeredConsumer>();
builder.Services.AddHostedService<JobStatusChangedConsumer>();
builder.Services.AddHostedService<OutboxRelayWorker>();

// Add CORS
builder.Services.AddCors(options => 
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? ["http://localhost:3000"])
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()));

var app = builder.Build();

// Programmatic Migration
using (var scope = app.Services.CreateScope())
{
    await FlowForge.Infrastructure.Persistence.DatabaseInitializer.InitializeDatabasesAsync(scope.ServiceProvider);
}

// Bootstrap Redis consumer groups
var bootstrapper = app.Services.GetRequiredService<IStreamBootstrapper>();
await bootstrapper.EnsureAsync(StreamNames.AutomationTriggered, "webapi");
await bootstrapper.EnsureAsync(StreamNames.JobStatusChanged, "webapi");

// Middleware
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/openapi/v1.json", "FlowForge Web API v1");
        c.RoutePrefix = "swagger";
    });
}

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseCors();
app.MapControllers();
app.MapHub<JobStatusHub>("/hubs/job-status");

// Health endpoints
// Liveness: always 200 — only checks the process is not hung
app.MapHealthChecks("/health/live",  new HealthCheckOptions { Predicate = _ => false });
// Readiness: runs postgres + redis checks
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = _ => true });

app.Run();
