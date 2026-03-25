using FlowForge.Infrastructure;
using FlowForge.Infrastructure.Auth;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.Redis;
using FlowForge.Infrastructure.Telemetry;
using FlowForge.JobAutomator.Cache;
using FlowForge.JobAutomator.Clients;
using FlowForge.JobAutomator.Evaluators;
using FlowForge.JobAutomator.Initialization;
using FlowForge.JobAutomator.Options;
using FlowForge.JobAutomator.Workers;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Quartz.Impl.AdoJobStore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://+:8080");

// Shared Infrastructure (Redis, etc.)
builder.Services.AddRedis(builder.Configuration);
builder.Services.AddEncryption();
builder.Services.AddFlowForgeTelemetry(builder.Configuration, "JobAutomator");

// Client-credentials token handler (M2M auth against WebApi)
builder.Services.AddKeycloakClientCredentials(builder.Configuration);

// HTTP client for one-time startup snapshot — token is attached automatically
builder.Services.AddHttpClient<IAutomationApiClient, AutomationApiClient>(client =>
    client.BaseAddress = new Uri(builder.Configuration["WebApi:BaseUrl"] ?? "http://localhost:5015"))
    .AddHttpMessageHandler<ClientCredentialsHandler>();

// In-memory cache (singleton)
builder.Services.AddSingleton<AutomationCache>();
builder.Services.AddSingleton<IQuartzScheduleSync, QuartzScheduleSync>();

// Quartz — clustered PostgreSQL job store
var quartzConnString = builder.Configuration.GetConnectionString("QuartzConnection")
    ?? throw new ArgumentNullException("QuartzConnection");

builder.Services.AddQuartz(q =>
{
    q.SchedulerId = "AUTO";
    q.SchedulerName = "FlowForgeScheduler";
    q.UsePersistentStore(store =>
    {
        store.UseProperties = true;
        store.UseGenericDatabase("Npgsql", db =>
        {
            db.ConnectionString = quartzConnString;
            db.UseDriverDelegate<PostgreSQLDelegate>();
        });
        store.UseNewtonsoftJsonSerializer();
        store.UseClustering(c =>
        {
            c.CheckinInterval = TimeSpan.FromSeconds(10);
        });
    });
});
builder.Services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

// Worker options
builder.Services.Configure<AutomationWorkerOptions>(
    builder.Configuration.GetSection(AutomationWorkerOptions.SectionName));

// CustomScript options
builder.Services.Configure<CustomScriptOptions>(
    builder.Configuration.GetSection(CustomScriptOptions.SectionName));

// Evaluators
builder.Services.AddSingleton<TriggerConditionEvaluator>();
builder.Services.AddSingleton<ITriggerEvaluator, ScheduleTriggerEvaluator>();
builder.Services.AddSingleton<ITriggerEvaluator, SqlTriggerEvaluator>();
builder.Services.AddSingleton<ITriggerEvaluator, JobCompletedTriggerEvaluator>();
builder.Services.AddSingleton<ITriggerEvaluator, WebhookTriggerEvaluator>();
builder.Services.AddSingleton<ITriggerEvaluator, CustomScriptTriggerEvaluator>();

// Workers
builder.Services.AddHostedService<AutomationCacheInitializer>();
builder.Services.AddHostedService<AutomationCacheSyncWorker>();
builder.Services.AddHostedService<JobCompletedFlagWorker>();
builder.Services.AddHostedService<AutomationWorker>();

// Health Checks
var redisConnStr = builder.Configuration["Redis:ConnectionString"]
    ?? throw new InvalidOperationException("Missing Redis:ConnectionString");

builder.Services.AddHealthChecks()
    .AddRedis(redisConnStr, name: "redis");

var app = builder.Build();

// Bootstrap Redis consumer groups
var bootstrapper = app.Services.GetRequiredService<IStreamBootstrapper>();
await bootstrapper.EnsureAsync(StreamNames.AutomationChanged, "job-automator");
await bootstrapper.EnsureAsync(StreamNames.JobStatusChanged, "job-automator-flags");

// Health endpoints
// Liveness: always 200 — only checks the process is not hung
app.MapHealthChecks("/health/live",  new HealthCheckOptions { Predicate = _ => false });
// Readiness: runs redis check
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = _ => true });

await app.RunAsync();
