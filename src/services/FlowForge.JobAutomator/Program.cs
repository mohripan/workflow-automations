using FlowForge.Infrastructure;
using FlowForge.JobAutomator.Evaluators;
using FlowForge.JobAutomator.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// Shared Infrastructure
builder.Services.AddInfrastructure(builder.Configuration);

// Evaluators
builder.Services.AddSingleton<TriggerConditionEvaluator>();
builder.Services.AddSingleton<ITriggerEvaluator, ScheduleTriggerEvaluator>();
builder.Services.AddSingleton<ITriggerEvaluator, SqlTriggerEvaluator>();
builder.Services.AddSingleton<ITriggerEvaluator, JobCompletedTriggerEvaluator>();
builder.Services.AddSingleton<ITriggerEvaluator, WebhookTriggerEvaluator>();

// Workers
builder.Services.AddHostedService<AutomationWorker>();

var host = builder.Build();
host.Run();
