using FlowForge.Infrastructure;
using FlowForge.Infrastructure.Messaging;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.Dapr;
using FlowForge.Infrastructure.Messaging.Redis;
using FlowForge.JobOrchestrator.Handlers;
using FlowForge.JobOrchestrator.LoadBalancing;
using FlowForge.JobOrchestrator.Options;
using FlowForge.JobOrchestrator.Workers;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://+:8080");

builder.Services.AddInfrastructure(builder.Configuration, "JobOrchestrator");

builder.Services.AddSingleton<ILoadBalancer, RoundRobinLoadBalancer>();

builder.Services.Configure<HeartbeatMonitorOptions>(
    builder.Configuration.GetSection(HeartbeatMonitorOptions.SectionName));

builder.Services.Configure<PendingJobScannerOptions>(
    builder.Configuration.GetSection(PendingJobScannerOptions.SectionName));

var messagingProvider = builder.Configuration
    .GetSection("Messaging")?.GetValue<string>("Provider") ?? "redis";

builder.Services.AddSingleton<JobCreatedHandler>();
builder.Services.AddSingleton<IEventHandler<FlowForge.Contracts.Events.JobCreatedEvent>>(sp =>
    sp.GetRequiredService<JobCreatedHandler>());

if (messagingProvider == "redis")
{
    builder.Services.AddHostedService<JobDispatcherWorker>();
}

builder.Services.AddHostedService<HeartbeatMonitorWorker>();
builder.Services.AddHostedService<PendingJobScannerWorker>();

// Health Checks
var pgConnStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:DefaultConnection");
var redisConnStr = builder.Configuration["Redis:ConnectionString"]
    ?? throw new InvalidOperationException("Missing Redis:ConnectionString");

builder.Services.AddHealthChecks()
    .AddNpgSql(pgConnStr, healthQuery: "SELECT 1;", name: "postgres")
    .AddRedis(redisConnStr, name: "redis");

var app = builder.Build();

// Bootstrap messaging infrastructure
if (messagingProvider == "redis")
{
    var bootstrapper = app.Services.GetRequiredService<IStreamBootstrapper>();
    await bootstrapper.EnsureAsync(StreamNames.JobCreated, "job-orchestrator");
}
else if (messagingProvider == "dapr")
{
    app.MapSubscribeHandler();
    app.MapDaprSubscription<FlowForge.Contracts.Events.JobCreatedEvent>(TopicNames.JobCreated);
}

// Health endpoints
// Liveness: always 200 — only checks the process is not hung
app.MapHealthChecks("/health/live",  new HealthCheckOptions { Predicate = _ => false });
// Readiness: runs postgres + redis checks
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = _ => true });

await app.RunAsync();
