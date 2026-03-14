namespace FlowForge.Infrastructure.MultiDb;

public record JobConnectionConfig(
    string ConnectionString,
    string Provider
);
