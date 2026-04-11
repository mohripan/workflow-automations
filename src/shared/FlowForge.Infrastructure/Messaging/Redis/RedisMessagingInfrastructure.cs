using FlowForge.Infrastructure.Messaging.Abstractions;
using StackExchange.Redis;

namespace FlowForge.Infrastructure.Messaging.Redis;

public class RedisMessagingInfrastructure(IConnectionMultiplexer redis) : IMessagingInfrastructure
{
    private readonly IDatabase _db = redis.GetDatabase();

    public async Task SetupAsync(IReadOnlyList<TopicSubscription> subscriptions, CancellationToken ct = default)
    {
        foreach (var sub in subscriptions)
        {
            var streamName = $"flowforge:{sub.TopicName}";
            try
            {
                await _db.StreamCreateConsumerGroupAsync(streamName, sub.GroupName, "$", createStream: true);
            }
            catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
            {
                // Group already exists — idempotent
            }
        }
    }
}
