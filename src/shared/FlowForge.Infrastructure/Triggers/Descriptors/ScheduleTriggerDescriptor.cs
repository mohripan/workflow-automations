using FlowForge.Domain.Enums;
using FlowForge.Domain.Triggers;
using System.Text.Json;

namespace FlowForge.Infrastructure.Triggers.Descriptors;

public class ScheduleTriggerDescriptor : ITriggerTypeDescriptor
{
    public string TypeId => TriggerTypes.Schedule;
    public string DisplayName => "Schedule";
    public string? Description => "Fires automatically on a cron schedule (UTC).";

    public TriggerConfigSchema GetSchema() => new(
        TypeId: TypeId,
        DisplayName: DisplayName,
        Description: Description,
        Fields:
        [
            new ConfigField(
                Name: "cronExpression",
                Label: "Cron Expression",
                DataType: ConfigFieldType.CronExpression,
                Required: true,
                Description: "Standard 6-part cron (seconds included). Example: '0 0 8 * * ?' fires at 08:00 UTC daily.",
                DefaultValue: "0 0 8 * * ?",
                EnumValues: null)
        ]);

    public IReadOnlyList<string> ValidateConfig(string configJson)
    {
        var errors = new List<string>();
        try
        {
            var cfg = JsonSerializer.Deserialize<ScheduleTriggerConfig>(configJson);
            if (string.IsNullOrWhiteSpace(cfg?.CronExpression))
                errors.Add("cronExpression is required.");
            else if (cfg.CronExpression.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 6)
                errors.Add($"'{cfg.CronExpression}' is not a valid cron expression (expected 6 parts).");
        }
        catch
        {
            errors.Add("configJson is not valid JSON.");
        }
        return errors;
    }

    private record ScheduleTriggerConfig(string? CronExpression);
}
