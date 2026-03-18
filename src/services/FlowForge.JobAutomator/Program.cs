using FlowForge.Infrastructure;
using FlowForge.JobAutomator.Cache;
using FlowForge.JobAutomator.Clients;
using FlowForge.JobAutomator.Evaluators;
using FlowForge.JobAutomator.Initialization;
using FlowForge.JobAutomator.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;

var builder = Host.CreateApplicationBuilder(args);

// Shared Infrastructure (Redis, etc.)
builder.Services.AddRedis(builder.Configuration);

// HTTP client for one-time startup snapshot
builder.Services.AddHttpClient<IAutomationApiClient, AutomationApiClient>(client =>
    client.BaseAddress = new Uri(builder.Configuration["WebApi:BaseUrl"] ?? "http://localhost:5015"));

// In-memory cache (singleton)
builder.Services.AddSingleton<AutomationCache>();
builder.Services.AddSingleton<IQuartzScheduleSync, QuartzScheduleSync>();

// Quartz
builder.Services.AddQuartz();
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
host.Run();
