using FlowForge.Contracts.Events;
using FlowForge.Domain.Triggers;
using FlowForge.Infrastructure.Caching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text.Json;

namespace FlowForge.JobAutomator.Evaluators;

public sealed class CustomScriptOptions
{
    public const string SectionName = "CustomScript";

    public string ScriptTempDir { get; init; } = "/tmp/flowforge/scripts";
    public string VenvCacheDir { get; init; } = "/tmp/flowforge/venvs";
    public string PythonPath { get; init; } = "python3";
}

public class CustomScriptTriggerEvaluator(
    IRedisService redis,
    IOptions<CustomScriptOptions> options,
    ILogger<CustomScriptTriggerEvaluator> logger) : ITriggerEvaluator
{
    public string TypeId => TriggerTypes.CustomScript;

    public async Task<bool> EvaluateAsync(TriggerSnapshot trigger, CancellationToken ct)
    {
        var config = JsonSerializer.Deserialize<CustomScriptTriggerConfig>(trigger.ConfigJson)!;

        var lastRunKey = $"trigger:custom-script:{trigger.Id}:last-run";
        var lastRunStr = await redis.GetAsync(lastRunKey);
        if (lastRunStr is not null)
        {
            var elapsed = DateTimeOffset.UtcNow - DateTimeOffset.Parse(lastRunStr);
            if (elapsed < TimeSpan.FromSeconds(config.PollingIntervalSeconds))
            {
                logger.LogDebug(
                    "Custom script trigger '{TriggerName}' skipped — within interval ({Interval}s)",
                    trigger.Name, config.PollingIntervalSeconds);
                return false;
            }
        }

        await redis.SetAsync(lastRunKey, DateTimeOffset.UtcNow.ToString("O"));
        return await RunScriptAsync(trigger, config, ct);
    }

    private async Task<bool> RunScriptAsync(
        TriggerSnapshot trigger, CustomScriptTriggerConfig config, CancellationToken ct)
    {
        Directory.CreateDirectory(options.Value.ScriptTempDir);
        var scriptPath = Path.Combine(options.Value.ScriptTempDir, $"trigger-{trigger.Id}.py");
        await File.WriteAllTextAsync(scriptPath, config.ScriptContent, ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 1, 60));
        cts.CancelAfter(timeout);

        try
        {
            var pythonExe = options.Value.PythonPath;
            if (!string.IsNullOrEmpty(config.Requirements))
                await EnsureVenvAsync(trigger, config.Requirements, ct);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(pythonExe)
                {
                    ArgumentList = { scriptPath },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            try { process.Start(); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Custom script trigger '{TriggerName}' could not start interpreter '{Python}'",
                    trigger.Name, pythonExe);
                return false;
            }
            var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = await process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            if (process.ExitCode != 0)
            {
                logger.LogWarning(
                    "Custom script trigger '{TriggerName}' exited with code {ExitCode}. Stderr: {Stderr}",
                    trigger.Name, process.ExitCode, stderr.Trim());
                return false;
            }

            var fired = stdout.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            logger.LogDebug(
                "Custom script trigger '{TriggerName}' stdout='{Stdout}', fired={Fired}",
                trigger.Name, stdout.Trim(), fired);
            return fired;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "Custom script trigger '{TriggerName}' timed out after {TimeoutSeconds}s",
                trigger.Name, config.TimeoutSeconds);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Custom script trigger '{TriggerName}' threw an unexpected exception", trigger.Name);
            return false;
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* ignore */ }
        }
    }

    private async Task EnsureVenvAsync(TriggerSnapshot trigger, string requirements, CancellationToken ct)
    {
        var venvDir = Path.Combine(options.Value.VenvCacheDir, trigger.Id.ToString("N"));
        var markerFile = Path.Combine(venvDir, ".installed");
        var reqHash = ComputeHash(requirements);

        if (File.Exists(markerFile) && await File.ReadAllTextAsync(markerFile, ct) == reqHash)
            return;

        logger.LogInformation("Installing pip requirements for trigger '{TriggerName}'", trigger.Name);

        Directory.CreateDirectory(venvDir);
        await RunCommandAsync(options.Value.PythonPath, ["-m", "venv", venvDir], ct);

        var reqFile = Path.Combine(venvDir, "requirements.txt");
        await File.WriteAllTextAsync(reqFile, requirements, ct);

        var pip = Path.Combine(venvDir, "bin", "pip");
        await RunCommandAsync(pip, ["install", "-r", reqFile, "--quiet"], ct);

        await File.WriteAllTextAsync(markerFile, reqHash, ct);
    }

    private static async Task RunCommandAsync(string exe, string[] args, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        await process.WaitForExitAsync(ct);
    }

    private static string ComputeHash(string input)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }

    private record CustomScriptTriggerConfig(
        string ScriptContent,
        string? Requirements = null,
        int PollingIntervalSeconds = 30,
        int TimeoutSeconds = 10);
}
