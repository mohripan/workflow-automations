using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FlowForge.WorkflowEngine.Handlers;

public class RunScriptHandler(ILogger<RunScriptHandler> logger) : IWorkflowHandler
{
    public string TaskId => "run-script";

    public async Task<WorkflowResult> ExecuteAsync(WorkflowContext context, CancellationToken ct)
    {
        var interpreter = context.GetParameter<string>("interpreter") ?? "cmd.exe";
        var scriptPath = context.GetParameter<string>("scriptPath");
        var arguments = context.GetParameter<string>("arguments") ?? "";

        logger.LogInformation("Running script {Path} with interpreter {Interpreter}", scriptPath, interpreter);

        var startInfo = new ProcessStartInfo
        {
            FileName = interpreter,
            Arguments = $"{scriptPath} {arguments}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        await process.WaitForExitAsync(ct);

        if (process.ExitCode == 0)
        {
            return WorkflowResult.Success();
        }

        var error = await process.StandardError.ReadToEndAsync(ct);
        return WorkflowResult.Failure($"Script exited with code {process.ExitCode}: {error}");
    }
}
