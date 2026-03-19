using FlowForge.Infrastructure;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.Redis;
using FlowForge.JobOrchestrator.LoadBalancing;
using FlowForge.JobOrchestrator.Workers;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration, "JobOrchestrator");

builder.Services.AddSingleton<ILoadBalancer, RoundRobinLoadBalancer>();

builder.Services.AddHostedService<JobDispatcherWorker>();
builder.Services.AddHostedService<HeartbeatMonitorWorker>();

var host = builder.Build();

// Bootstrap Redis consumer groups
var bootstrapper = host.Services.GetRequiredService<IStreamBootstrapper>();
await bootstrapper.EnsureAsync(StreamNames.JobCreated, "job-orchestrator");

host.Run();
