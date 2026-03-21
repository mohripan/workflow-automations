using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FlowForge.WorkflowEngine.Handlers;

public class RunScriptHandler(ILogger<RunScriptHandler> logger) : IWorkflowHandler
{
    public string TaskId => "run-script";

    public async Task<WorkflowResult> ExecuteAsync(WorkflowContext context, CancellationToken ct)
    {
        var interpreter = context.Parameters.TryGetValue("interpreter", out var intEl)
            ? intEl.GetString() ?? "python3"
            : "python3";
        var scriptPath = context.GetParameter<string>("scriptPath");
        var arguments = context.Parameters.TryGetValue("arguments", out var argEl)
            ? argEl.GetString() ?? ""
            : "";

        // Optional: pip packages to install before running the script.
        // Expects a JSON array, e.g. ["resend", "requests"].
        // Packages are installed using the same interpreter to guarantee the
        // right environment: `<interpreter> -m pip install <packages>`.
        if (context.Parameters.TryGetValue("packages", out var packagesEl) &&
            packagesEl.ValueKind == JsonValueKind.Array)
        {
            var packages = packagesEl.Deserialize<string[]>() ?? [];
            if (packages.Length > 0)
            {
                logger.LogInformation("Installing packages via {Interpreter}: {Packages}",
                    interpreter, string.Join(", ", packages));

                var pipInfo = new ProcessStartInfo
                {
                    FileName = interpreter,
                    Arguments = $"-m pip install {string.Join(" ", packages)}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var pipProcess = new Process { StartInfo = pipInfo };
                pipProcess.Start();
                await pipProcess.WaitForExitAsync(ct);

                if (pipProcess.ExitCode != 0)
                {
                    var pipErr = await pipProcess.StandardError.ReadToEndAsync(ct);
                    return WorkflowResult.Failure($"Package install failed (exit {pipProcess.ExitCode}): {pipErr}");
                }

                logger.LogInformation("Packages installed successfully");
            }
        }

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

        // Optional: per-automation environment variable overrides.
        // Expects a JSON object, e.g. {"RESEND_API_KEY": "re_...", "EMAIL_TO": "..."}.
        if (context.Parameters.TryGetValue("env", out var envEl) &&
            envEl.ValueKind == JsonValueKind.Object)
        {
            var envVars = envEl.Deserialize<Dictionary<string, string>>() ?? [];
            foreach (var (key, value) in envVars)
                startInfo.Environment[key] = value;
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        await process.WaitForExitAsync(ct);

        if (process.ExitCode == 0)
            return WorkflowResult.Success();

        var error = await process.StandardError.ReadToEndAsync(ct);
        return WorkflowResult.Failure($"Script exited with code {process.ExitCode}: {error}");
    }
}
