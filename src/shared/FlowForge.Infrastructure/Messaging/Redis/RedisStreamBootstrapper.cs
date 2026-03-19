using FlowForge.Infrastructure.Messaging.Abstractions;
using StackExchange.Redis;

namespace FlowForge.Infrastructure.Messaging.Redis;

public class RedisStreamBootstrapper(IConnectionMultiplexer redis) : IStreamBootstrapper
{
    private readonly IDatabase _db = redis.GetDatabase();

    public async Task EnsureAsync(string streamName, string groupName, CancellationToken ct = default)
    {
        try
        {
            await _db.StreamCreateConsumerGroupAsync(streamName, groupName, "$", createStream: true);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
        {
            // Group already exists — idempotent
        }
    }
}
