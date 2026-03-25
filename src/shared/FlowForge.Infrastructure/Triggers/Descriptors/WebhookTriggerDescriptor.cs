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
        "Optionally validates an HMAC-SHA256 signature in the X-FlowForge-Signature header.";

    public TriggerConfigSchema GetSchema() => new(
        TypeId: TypeId,
        DisplayName: DisplayName,
        Description: Description,
        Fields:
        [
            new ConfigField(
                Name: "secret",
                Label: "Webhook Secret (optional)",
                DataType: ConfigFieldType.String,
                Required: false,
                Description: "Shared secret used to verify the HMAC-SHA256 signature sent in the " +
                             "X-FlowForge-Signature header. Stored encrypted at rest. " +
                             "Leave blank to accept unauthenticated webhooks.",
                DefaultValue: null,
                EnumValues: null)
        ]);

    /// <summary>
    /// Marks <c>secret</c> as sensitive so the service layer encrypts it at rest
    /// and redacts it in API responses.
    /// </summary>
    public IReadOnlyList<string> GetSensitiveFieldNames() => ["secret"];

    public IReadOnlyList<string> ValidateConfig(string configJson)
    {
        try { JsonSerializer.Deserialize<WebhookTriggerConfig>(configJson); }
        catch { return ["configJson is not valid JSON."]; }
        return [];
    }

    private record WebhookTriggerConfig(string? Secret);
}
