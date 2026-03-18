using FlowForge.Domain.Enums;
using FlowForge.Domain.Triggers;
using System.Text.Json;

namespace FlowForge.Infrastructure.Triggers.Descriptors;

public class JobCompletedTriggerDescriptor : ITriggerTypeDescriptor
{
    public string TypeId => TriggerTypes.JobCompleted;
    public string DisplayName => "Job Completed";
    public string? Description => "Fires when a job belonging to a specific automation completes successfully.";

    public TriggerConfigSchema GetSchema() => new(
        TypeId: TypeId,
        DisplayName: DisplayName,
        Description: Description,
        Fields:
        [
            new ConfigField(
                Name: "watchAutomationId",
                Label: "Watch Automation",
                DataType: ConfigFieldType.String,
                Required: true,
                Description: "ID (GUID) of the automation whose job completion triggers this.",
                DefaultValue: null,
                EnumValues: null)
        ]);

    public IReadOnlyList<string> ValidateConfig(string configJson)
    {
        var errors = new List<string>();
        try
        {
            var cfg = JsonSerializer.Deserialize<JobCompletedTriggerConfig>(configJson);
            if (cfg?.WatchAutomationId == Guid.Empty || cfg?.WatchAutomationId == null)
                errors.Add("watchAutomationId is required and must be a valid GUID.");
        }
        catch { errors.Add("configJson is not valid JSON."); }
        return errors;
    }

    private record JobCompletedTriggerConfig(Guid? WatchAutomationId);
}
