using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FlowForge.WorkflowHost.ProcessManagement;

public class NativeProcessManager(
    IConfiguration configuration,
    ILogger<NativeProcessManager> logger) : IProcessManager
{
    public async Task RunAsync(Guid jobId, Guid automationId, string connectionId, CancellationToken ct)
    {
        var enginePath = configuration["WorkflowHost:EnginePath"] ?? "FlowForge.WorkflowEngine.exe";
        var redisConn = configuration["Redis:ConnectionString"] ?? "localhost:6379";

        var startInfo = new ProcessStartInfo
        {
            FileName = enginePath,
            Arguments = $"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // Pass configuration via environment variables as per WORKFLOWENGINE.md
        startInfo.EnvironmentVariables["JOB_ID"] = jobId.ToString();
        startInfo.EnvironmentVariables["JOB_AUTOMATION_ID"] = automationId.ToString();
        startInfo.EnvironmentVariables["CONNECTION_ID"] = connectionId;
        startInfo.EnvironmentVariables["REDIS_CONNECTION"] = redisConn;

        using var process = new Process { StartInfo = startInfo };

        logger.LogInformation("Starting WorkflowEngine for job {JobId}", jobId);
        
        process.Start();

        var outputTask = ConsumeStreamAsync(process.StandardOutput, l => logger.LogInformation("[Engine-{JobId}] {Line}", jobId, l));
        var errorTask = ConsumeStreamAsync(process.StandardError, l => logger.LogError("[Engine-{JobId}] {Line}", jobId, l));

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Killing WorkflowEngine for job {JobId} due to cancellation", jobId);
            process.Kill(true);
        }

        await Task.WhenAll(outputTask, errorTask);
        logger.LogInformation("WorkflowEngine for job {JobId} exited with code {ExitCode}", jobId, process.ExitCode);
    }

    private static async Task ConsumeStreamAsync(StreamReader reader, Action<string> logAction)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            logAction(line);
        }
    }
}
