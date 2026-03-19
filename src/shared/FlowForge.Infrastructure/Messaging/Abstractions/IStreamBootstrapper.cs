namespace FlowForge.Infrastructure.Messaging.Abstractions;

public interface IStreamBootstrapper
{
    Task EnsureAsync(string streamName, string groupName, CancellationToken ct = default);
}
