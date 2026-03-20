using FlowForge.Infrastructure;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.Redis;
using FlowForge.WorkflowHost.Options;
using FlowForge.WorkflowHost.ProcessManagement;
using FlowForge.WorkflowHost.Workers;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://+:8080");

builder.Services.AddInfrastructure(builder.Configuration, "WorkflowHost");

builder.Services.AddSingleton<IProcessManager, NativeProcessManager>();

builder.Services.Configure<HostHeartbeatOptions>(
    builder.Configuration.GetSection(HostHeartbeatOptions.SectionName));

builder.Services.AddHostedService<JobConsumerWorker>();
builder.Services.AddHostedService<CancelConsumerWorker>();
builder.Services.AddHostedService<HostHeartbeatWorker>();

// Health Checks
var pgConnStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:DefaultConnection");
var redisConnStr = builder.Configuration["Redis:ConnectionString"]
    ?? throw new InvalidOperationException("Missing Redis:ConnectionString");

builder.Services.AddHealthChecks()
    .AddNpgSql(pgConnStr, healthQuery: "SELECT 1;", name: "postgres")
    .AddRedis(redisConnStr, name: "redis");

var app = builder.Build();

// Bootstrap Redis consumer groups
var hostId = Environment.GetEnvironmentVariable("NODE_NAME") ?? Environment.MachineName;
var bootstrapper = app.Services.GetRequiredService<IStreamBootstrapper>();
await bootstrapper.EnsureAsync(StreamNames.HostStream(hostId), "workflow-host");
await bootstrapper.EnsureAsync(StreamNames.JobCancelRequested, "workflow-host");

// Health endpoints
// Liveness: always 200 — only checks the process is not hung
app.MapHealthChecks("/health/live",  new HealthCheckOptions { Predicate = _ => false });
// Readiness: runs postgres + redis checks
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = _ => true });

await app.RunAsync();
