namespace FlowForge.WebApi.Options;

public class OutboxRelayOptions
{
    public const string SectionName = "OutboxRelay";

    public int PollIntervalMs { get; init; } = 500;
    public int BatchSize { get; init; } = 50;
}
