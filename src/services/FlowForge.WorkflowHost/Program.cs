using FlowForge.Infrastructure;
using FlowForge.WorkflowHost.ProcessManagement;
using FlowForge.WorkflowHost.Workers;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddSingleton<IProcessManager, NativeProcessManager>();

builder.Services.AddHostedService<JobConsumerWorker>();
builder.Services.AddHostedService<CancelConsumerWorker>();
builder.Services.AddHostedService<HostHeartbeatWorker>();

var host = builder.Build();
host.Run();
