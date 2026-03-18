using FlowForge.Domain.Enums;
using FlowForge.Domain.Triggers;
using System.Text.Json;

namespace FlowForge.Infrastructure.Triggers.Descriptors;

public class CustomScriptTriggerDescriptor : ITriggerTypeDescriptor
{
    public string TypeId => TriggerTypes.CustomScript;
    public string DisplayName => "Custom Script";
    public string? Description =>
        "Runs a Python script on a polling interval. " +
        "The trigger fires when the script exits with code 0 and prints 'true' to stdout.";

    public TriggerConfigSchema GetSchema() => new(
        TypeId: TypeId,
        DisplayName: DisplayName,
        Description: Description,
        Fields:
        [
            new ConfigField(
                Name: "scriptContent",
                Label: "Python Script",
                DataType: ConfigFieldType.Script,
                Required: true,
                Description: "Python 3 script. Print 'true' to fire the trigger, anything else (or exit non-zero) to skip.",
                DefaultValue: "# Return 'true' to fire the trigger\nprint('false')",
                EnumValues: null),

            new ConfigField(
                Name: "requirements",
                Label: "pip Requirements",
                DataType: ConfigFieldType.MultilineString,
                Required: false,
                Description: "Newline-separated pip packages to install before running. " +
                             "Example: 'requests==2.32.3'. Packages are cached across runs.",
                DefaultValue: null,
                EnumValues: null),

            new ConfigField(
                Name: "pollingIntervalSeconds",
                Label: "Polling Interval (seconds)",
                DataType: ConfigFieldType.Int,
                Required: false,
                Description: "How often the script is run. Minimum 5 seconds.",
                DefaultValue: "30",
                EnumValues: null),

            new ConfigField(
                Name: "timeoutSeconds",
                Label: "Script Timeout (seconds)",
                DataType: ConfigFieldType.Int,
                Required: false,
                Description: "Maximum time the script is allowed to run before it is killed. Maximum 60 seconds.",
                DefaultValue: "10",
                EnumValues: null)
        ]);

    public IReadOnlyList<string> ValidateConfig(string configJson)
    {
        var errors = new List<string>();
        try
        {
            var cfg = JsonSerializer.Deserialize<CustomScriptTriggerConfig>(configJson);
            if (string.IsNullOrWhiteSpace(cfg?.ScriptContent))
                errors.Add("scriptContent is required.");
            if (cfg?.PollingIntervalSeconds < 5)
                errors.Add("pollingIntervalSeconds must be at least 5.");
            if (cfg?.TimeoutSeconds is < 1 or > 60)
                errors.Add("timeoutSeconds must be between 1 and 60.");
        }
        catch { errors.Add("configJson is not valid JSON."); }
        return errors;
    }

    private record CustomScriptTriggerConfig(
        string? ScriptContent,
        string? Requirements,
        int? PollingIntervalSeconds,
        int? TimeoutSeconds);
}
