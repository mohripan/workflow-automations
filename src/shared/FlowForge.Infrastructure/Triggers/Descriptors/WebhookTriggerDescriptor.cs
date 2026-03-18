using FlowForge.Domain.Enums;
using FlowForge.Domain.Triggers;
using System.Text.Json;

namespace FlowForge.Infrastructure.Triggers.Descriptors;

public class WebhookTriggerDescriptor : ITriggerTypeDescriptor
{
    public string TypeId => TriggerTypes.Webhook;
    public string DisplayName => "Webhook";
    public string? Description =>
        "Fires when an external system POSTs to /api/automations/{id}/webhook. " +
        "Optionally validates a shared secret header.";

    public TriggerConfigSchema GetSchema() => new(
        TypeId: TypeId,
        DisplayName: DisplayName,
        Description: Description,
        Fields:
        [
            new ConfigField(
                Name: "secretHash",
                Label: "Webhook Secret (optional)",
                DataType: ConfigFieldType.String,
                Required: false,
                Description: "BCrypt hash of the secret value that callers must pass in the X-Webhook-Secret header. " +
                             "Leave blank to accept unauthenticated webhooks.",
                DefaultValue: null,
                EnumValues: null)
        ]);

    public IReadOnlyList<string> ValidateConfig(string configJson)
    {
        try { JsonSerializer.Deserialize<WebhookTriggerConfig>(configJson); }
        catch { return ["configJson is not valid JSON."]; }
        return [];
    }

    private record WebhookTriggerConfig(string? SecretHash);
}
