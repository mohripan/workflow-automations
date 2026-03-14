using FlowForge.Infrastructure.Caching;
using StackExchange.Redis;

namespace FlowForge.Infrastructure.Caching;

public class RedisService(IConnectionMultiplexer redis) : IRedisService
{
    private readonly IDatabase _db = redis.GetDatabase();

    public async Task SetAsync(string key, string value, TimeSpan? expiry = null)
    {
        await _db.StringSetAsync(key, value, expiry);
    }

    public async Task<string?> GetAsync(string key)
    {
        return await _db.StringGetAsync(key);
    }

    public async Task DeleteAsync(string key)
    {
        await _db.KeyDeleteAsync(key);
    }

    public async Task RefreshHeartbeatAsync(Guid jobId, TimeSpan ttl)
    {
        await _db.StringSetAsync($"heartbeat:{jobId}", "alive", ttl);
    }

    public async Task<bool> IsHeartbeatAliveAsync(Guid jobId)
    {
        return await _db.KeyExistsAsync($"heartbeat:{jobId}");
    }
}
