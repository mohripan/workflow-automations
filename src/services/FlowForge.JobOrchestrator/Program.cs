using FlowForge.Infrastructure;
using FlowForge.JobOrchestrator.LoadBalancing;
using FlowForge.JobOrchestrator.Workers;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddSingleton<ILoadBalancer, RoundRobinLoadBalancer>();

builder.Services.AddHostedService<JobDispatcherWorker>();
builder.Services.AddHostedService<HeartbeatMonitorWorker>();

var host = builder.Build();
host.Run();
