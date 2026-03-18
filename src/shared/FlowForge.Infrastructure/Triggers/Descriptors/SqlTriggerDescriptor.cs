using FlowForge.Domain.Enums;
using FlowForge.Domain.Triggers;
using System.Text.Json;

namespace FlowForge.Infrastructure.Triggers.Descriptors;

public class SqlTriggerDescriptor : ITriggerTypeDescriptor
{
    public string TypeId => TriggerTypes.Sql;
    public string DisplayName => "SQL Query";
    public string? Description => "Fires when a SQL query returns at least one row, and the result has changed since the last check.";

    public TriggerConfigSchema GetSchema() => new(
        TypeId: TypeId,
        DisplayName: DisplayName,
        Description: Description,
        Fields:
        [
            new ConfigField(
                Name: "connectionString",
                Label: "Connection String",
                DataType: ConfigFieldType.ConnectionString,
                Required: true,
                Description: "Connection string for the external database to query (not the FlowForge DB).",
                DefaultValue: null,
                EnumValues: null),

            new ConfigField(
                Name: "query",
                Label: "SQL Query",
                DataType: ConfigFieldType.MultilineString,
                Required: true,
                Description: "SELECT query to run. Fires when the result set is non-empty and different from the previous run.",
                DefaultValue: "SELECT id FROM your_table WHERE condition = true LIMIT 1",
                EnumValues: null),

            new ConfigField(
                Name: "pollingIntervalSeconds",
                Label: "Polling Interval (seconds)",
                DataType: ConfigFieldType.Int,
                Required: false,
                Description: "How often to run the query. Minimum 5 seconds.",
                DefaultValue: "30",
                EnumValues: null)
        ]);

    public IReadOnlyList<string> ValidateConfig(string configJson)
    {
        var errors = new List<string>();
        try
        {
            var cfg = JsonSerializer.Deserialize<SqlTriggerConfig>(configJson);
            if (string.IsNullOrWhiteSpace(cfg?.ConnectionString)) errors.Add("connectionString is required.");
            if (string.IsNullOrWhiteSpace(cfg?.Query)) errors.Add("query is required.");
            if (cfg?.PollingIntervalSeconds < 5) errors.Add("pollingIntervalSeconds must be at least 5.");
        }
        catch { errors.Add("configJson is not valid JSON."); }
        return errors;
    }

    private record SqlTriggerConfig(string? ConnectionString, string? Query, int? PollingIntervalSeconds);
}
