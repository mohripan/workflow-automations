namespace FlowForge.Infrastructure.Caching;

public interface IRedisService
{
    Task SetAsync(string key, string value, TimeSpan? expiry = null);
    Task<string?> GetAsync(string key);
    Task DeleteAsync(string key);
    Task RefreshHeartbeatAsync(Guid jobId, TimeSpan ttl);
    Task<bool> IsHeartbeatAliveAsync(Guid jobId);
}
