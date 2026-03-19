using FlowForge.Infrastructure;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.Redis;
using FlowForge.WorkflowHost.ProcessManagement;
using FlowForge.WorkflowHost.Workers;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddSingleton<IProcessManager, NativeProcessManager>();

builder.Services.AddHostedService<JobConsumerWorker>();
builder.Services.AddHostedService<CancelConsumerWorker>();
builder.Services.AddHostedService<HostHeartbeatWorker>();

var host = builder.Build();

// Bootstrap Redis consumer groups
var hostId = Environment.GetEnvironmentVariable("NODE_NAME") ?? Environment.MachineName;
var bootstrapper = host.Services.GetRequiredService<IStreamBootstrapper>();
await bootstrapper.EnsureAsync(StreamNames.HostStream(hostId), "workflow-host");
await bootstrapper.EnsureAsync(StreamNames.JobCancelRequested, "workflow-host");

host.Run();
