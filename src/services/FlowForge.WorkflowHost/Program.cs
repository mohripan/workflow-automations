using FlowForge.Domain.Entities;
using FlowForge.Domain.Repositories;
using FlowForge.Infrastructure;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.Redis;
using FlowForge.WorkflowHost.Options;
using FlowForge.WorkflowHost.ProcessManagement;
using FlowForge.WorkflowHost.Workers;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://+:8080");

builder.Services.AddInfrastructure(builder.Configuration, "WorkflowHost");

builder.Services.AddSingleton<IProcessManager, NativeProcessManager>();

builder.Services.Configure<HostHeartbeatOptions>(
    builder.Configuration.GetSection(HostHeartbeatOptions.SectionName));

// Register JobConsumerWorker as singleton so CancelConsumerWorker can inject it directly
builder.Services.AddSingleton<JobConsumerWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobConsumerWorker>());
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

// --- Self-registration: ensure this host exists in the platform DB ---
var hostId = Environment.GetEnvironmentVariable("NODE_NAME") ?? Environment.MachineName;
var hostGroupId = Environment.GetEnvironmentVariable("HOST_GROUP_ID");
var registrationToken = Environment.GetEnvironmentVariable("REGISTRATION_TOKEN");
var hostConnectionId = Environment.GetEnvironmentVariable("HOST_CONNECTION_ID");
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

using (var scope = app.Services.CreateScope())
{
    var hostRepo = scope.ServiceProvider.GetRequiredService<IWorkflowHostRepository>();
    var hostGroupRepo = scope.ServiceProvider.GetRequiredService<IHostGroupRepository>();

    var existingHost = await hostRepo.GetByNameAsync(hostId);

    if (existingHost is null)
    {
        Guid? resolvedGroupId = null;

        // Mode 1: Direct registration via HOST_GROUP_ID
        if (Guid.TryParse(hostGroupId, out var gid))
        {
            var group = await hostGroupRepo.GetByIdAsync(gid);
            if (group is not null) resolvedGroupId = gid;
            else logger.LogWarning("HOST_GROUP_ID {GroupId} not found in platform DB", hostGroupId);
        }
        // Mode 2: Token-based registration via REGISTRATION_TOKEN
        else if (!string.IsNullOrEmpty(registrationToken))
        {
            var groups = await hostGroupRepo.GetAllWithTokenAsync();
            var matchedGroup = groups.FirstOrDefault(g => g.ValidateRegistrationToken(registrationToken));
            if (matchedGroup is not null) resolvedGroupId = matchedGroup.Id;
            else logger.LogWarning("REGISTRATION_TOKEN did not match any host group");
        }
        // Mode 3: Lookup by HOST_CONNECTION_ID (matches HostGroup.ConnectionId)
        else if (!string.IsNullOrEmpty(hostConnectionId))
        {
            var allGroups = await hostGroupRepo.GetAllAsync();
            var matchedGroup = allGroups.FirstOrDefault(g =>
                string.Equals(g.ConnectionId, hostConnectionId, StringComparison.OrdinalIgnoreCase));
            if (matchedGroup is not null) resolvedGroupId = matchedGroup.Id;
            else logger.LogWarning("HOST_CONNECTION_ID '{ConnId}' did not match any host group", hostConnectionId);
        }

        if (resolvedGroupId.HasValue)
        {
            var newHost = WorkflowHost.Create(hostId, resolvedGroupId.Value);
            await hostRepo.SaveAsync(newHost);
            logger.LogInformation("Host {HostName} registered to group {GroupId}", hostId, resolvedGroupId.Value);
        }
        else
        {
            logger.LogWarning(
                "Host {HostName} could not self-register. Set HOST_GROUP_ID, REGISTRATION_TOKEN, or HOST_CONNECTION_ID. " +
                "The host will send heartbeats but won't receive jobs until registered.", hostId);
        }
    }
    else
    {
        logger.LogInformation("Host {HostName} already registered (group {GroupId})", hostId, existingHost.HostGroupId);
    }
}

// Bootstrap Redis consumer groups
var bootstrapper = app.Services.GetRequiredService<IStreamBootstrapper>();
await bootstrapper.EnsureAsync(StreamNames.HostStream(hostId), "workflow-host");
await bootstrapper.EnsureAsync(StreamNames.JobCancelRequested, "workflow-host");

// Health endpoints
// Liveness: always 200 — only checks the process is not hung
app.MapHealthChecks("/health/live",  new HealthCheckOptions { Predicate = _ => false });
// Readiness: runs postgres + redis checks
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = _ => true });

await app.RunAsync();
