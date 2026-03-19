using FlowForge.Infrastructure.Persistence.Platform;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace FlowForge.WebApi.Workers;

public class OutboxRelayWorker(
    IConnectionMultiplexer redis,
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxRelayWorker> logger) : BackgroundService
{
    private readonly IDatabase _redisDb = redis.GetDatabase();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RelayBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox relay pass failed");
            }

            await Task.Delay(500, stoppingToken);
        }
    }

    private async Task RelayBatchAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var messages = await db.OutboxMessages
            .Where(m => m.SentAt == null)
            .OrderBy(m => m.CreatedAt)
            .Take(50)
            .ToListAsync(ct);

        if (messages.Count == 0) return;

        foreach (var msg in messages)
        {
            await _redisDb.StreamAddAsync(msg.StreamName, "payload", msg.Payload);
            msg.MarkSent();
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Outbox relay published {Count} message(s)", messages.Count);
    }
}
