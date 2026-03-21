using System.Diagnostics;
using System.Text.Json;
using FlowForge.Domain.Enums;
using FlowForge.Domain.Repositories;
using FlowForge.Infrastructure;
using FlowForge.Infrastructure.Telemetry;
using FlowForge.WorkflowEngine.Handlers;
using FlowForge.WorkflowEngine.Reporting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var jobIdStr = Environment.GetEnvironmentVariable("JOB_ID");
var automationIdStr = Environment.GetEnvironmentVariable("JOB_AUTOMATION_ID");
var connectionId = Environment.GetEnvironmentVariable("CONNECTION_ID");

if (string.IsNullOrEmpty(jobIdStr) || string.IsNullOrEmpty(connectionId) || string.IsNullOrEmpty(automationIdStr))
{
    Console.WriteLine("JOB_ID, JOB_AUTOMATION_ID and CONNECTION_ID must be set");
    return 1;
}

var jobId = Guid.Parse(jobIdStr);
var automationId = Guid.Parse(automationIdStr);

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration, "WorkflowEngine");
builder.Services.AddHttpClient();

builder.Services.AddSingleton<WorkflowHandlerRegistry>();
builder.Services.AddScoped<IWorkflowHandler, HttpRequestHandler>();
builder.Services.AddScoped<IWorkflowHandler, RunScriptHandler>();

builder.Services.AddScoped<IJobReporter, JobProgressReporter>();

var host = builder.Build();

using var scope = host.Services.CreateScope();
var services = scope.ServiceProvider;
var registry = services.GetRequiredService<WorkflowHandlerRegistry>();
var reporter = services.GetRequiredService<IJobReporter>();
var jobRepo = services.GetRequiredKeyedService<IJobRepository>(connectionId);

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
CancellationTokenSource? timeoutCts = null;
int? timeoutSeconds = null;

try
{
    var job = await jobRepo.GetByIdAsync(jobId, cts.Token);
    if (job == null)
    {
        Console.WriteLine($"Job {jobId} not found");
        return 1;
    }

    if (job.TimeoutSeconds.HasValue)
    {
        timeoutSeconds = job.TimeoutSeconds;
        timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds.Value));
        cts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeoutCts.Token);
    }

    // Start heartbeat loop
    _ = Task.Run(async () =>
    {
        while (!cts.Token.IsCancellationRequested)
        {
            await reporter.RefreshHeartbeatAsync(jobId, cts.Token);
            await Task.Delay(5000, cts.Token);
        }
    }, cts.Token);

    await reporter.ReportStatusAsync(jobId, automationId, connectionId, JobStatus.InProgress, ct: cts.Token);

    var parameters = job.TaskConfig is not null
        ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(job.TaskConfig) ?? []
        : new Dictionary<string, JsonElement>();

    var context = new WorkflowContext
    {
        JobId = job.Id,
        TaskId = job.TaskId,
        ConnectionId = connectionId,
        Parameters = parameters
    };

    var engineSource = new ActivitySource("FlowForge.WorkflowEngine");
    using var activity = engineSource.StartActivity($"execute job {jobId}");

    var handler = registry.Get(job.TaskId);
    var sw = Stopwatch.StartNew();
    var result = await handler.ExecuteAsync(context, cts.Token);
    sw.Stop();

    FlowForge.Infrastructure.Telemetry.FlowForgeMetrics.JobDurationSeconds.Record(
        sw.Elapsed.TotalSeconds,
        new KeyValuePair<string, object?>("task_id", job.TaskId));

    var finalStatus = result.Status switch
    {
        WorkflowResultStatus.Completed => JobStatus.Completed,
        WorkflowResultStatus.Failed => JobStatus.CompletedUnsuccessfully,
        WorkflowResultStatus.Cancelled => JobStatus.Cancelled,
        _ => JobStatus.Error
    };

    var outputJson = context.Outputs.Count > 0
        ? JsonSerializer.Serialize(context.Outputs)
        : null;

    await reporter.ReportStatusAsync(jobId, automationId, connectionId, finalStatus, result.Message, outputJson, CancellationToken.None);
    return result.Status == WorkflowResultStatus.Error ? 1 : 0;
}
catch (OperationCanceledException)
{
    if (timeoutCts?.IsCancellationRequested == true)
    {
        await reporter.ReportStatusAsync(jobId, automationId, connectionId, JobStatus.Error,
            $"Job timed out after {timeoutSeconds} seconds.", null, CancellationToken.None);
        return 1;
    }
    await reporter.ReportStatusAsync(jobId, automationId, connectionId, JobStatus.Cancelled, "Cancelled", null, CancellationToken.None);
    return 0;
}
catch (Exception ex)
{
    await reporter.ReportStatusAsync(jobId, automationId, connectionId, JobStatus.Error, ex.Message, null, CancellationToken.None);
    return 1;
}
