namespace FlowForge.Domain.Tasks;

public record TaskParameterField(
    string Name,
    string Label,
    string Type,            // "text", "textarea", "number", "boolean"
    bool Required,
    string? DefaultValue = null,
    string? HelpText = null
);
