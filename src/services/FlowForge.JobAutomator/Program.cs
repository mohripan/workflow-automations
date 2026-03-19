using FlowForge.Infrastructure;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.Redis;
using FlowForge.Infrastructure.Telemetry;
using FlowForge.JobAutomator.Cache;
using FlowForge.JobAutomator.Clients;
using FlowForge.JobAutomator.Evaluators;
using FlowForge.JobAutomator.Initialization;
using FlowForge.JobAutomator.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using Quartz.Impl.AdoJobStore;

var builder = Host.CreateApplicationBuilder(args);

// Shared Infrastructure (Redis, etc.)
builder.Services.AddRedis(builder.Configuration);
builder.Services.AddFlowForgeTelemetry(builder.Configuration, "JobAutomator");

// HTTP client for one-time startup snapshot
builder.Services.AddHttpClient<IAutomationApiClient, AutomationApiClient>(client =>
    client.BaseAddress = new Uri(builder.Configuration["WebApi:BaseUrl"] ?? "http://localhost:5015"));

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

var host = builder.Build();

// Bootstrap Redis consumer groups
var bootstrapper = host.Services.GetRequiredService<IStreamBootstrapper>();
await bootstrapper.EnsureAsync(StreamNames.AutomationChanged, "job-automator");
await bootstrapper.EnsureAsync(StreamNames.JobStatusChanged, "job-automator-flags");

host.Run();
