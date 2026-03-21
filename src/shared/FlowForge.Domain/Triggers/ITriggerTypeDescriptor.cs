namespace FlowForge.Domain.Triggers;

public interface ITriggerTypeDescriptor
{
    string TypeId { get; }
    string DisplayName { get; }
    string? Description { get; }
    TriggerConfigSchema GetSchema();
    IReadOnlyList<string> ValidateConfig(string configJson);

    /// <summary>
    /// Names of configJson fields whose values contain secrets (passwords, tokens, etc.)
    /// that must be encrypted at rest and redacted in API responses.
    /// </summary>
    IReadOnlyList<string> GetSensitiveFieldNames() => [];
}
