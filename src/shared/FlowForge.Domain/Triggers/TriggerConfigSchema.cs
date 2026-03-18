using FlowForge.Domain.Enums;

namespace FlowForge.Domain.Triggers;

public record TriggerConfigSchema(
    string TypeId,
    string DisplayName,
    string? Description,
    IReadOnlyList<ConfigField> Fields
);

public record ConfigField(
    string Name,
    string Label,
    ConfigFieldType DataType,
    bool Required,
    string? Description,
    string? DefaultValue,
    IReadOnlyList<string>? EnumValues
);
