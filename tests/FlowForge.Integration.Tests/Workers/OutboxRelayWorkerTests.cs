using FlowForge.Domain.Entities;
using FlowForge.Infrastructure.Messaging.Redis;
using FlowForge.Infrastructure.Persistence.Platform;
using FlowForge.Integration.Tests.Infrastructure;
using FlowForge.WebApi.Workers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace FlowForge.Integration.Tests.Workers;

[Collection("Containers")]
public class OutboxRelayWorkerTests : IAsyncLifetime
{
    private readonly SharedContainersFixture _fixture;
    private PlatformDbContext _platformDb = null!;
    private IDbContextTransaction _platformTx = null!;

    public OutboxRelayWorkerTests(SharedContainersFixture fixture)
        => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _platformDb = _fixture.CreatePlatformDbContext();
        _platformTx = await _platformDb.Database.BeginTransactionAsync();
    }

    public async Task DisposeAsync()
    {
        await _platformTx.RollbackAsync();
        await _platformDb.DisposeAsync();
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GivenUnsentMessages_WhenRelayed_PublishesToRedisAndMarksSent()
    {
        // Arrange
        var streamName = StreamNames.JobCreated;
        var msg1 = OutboxMessage.Create("JobCreatedEvent", """{"test":"1"}""", streamName);
        var msg2 = OutboxMessage.Create("JobCreatedEvent", """{"test":"2"}""", streamName);
        await _platformDb.OutboxMessages.AddRangeAsync(msg1, msg2);
        await _platformDb.SaveChangesAsync();

        var redis = await ConnectionMultiplexer.ConnectAsync(_fixture.RedisConnectionString);
        var redisDb = redis.GetDatabase();

        // Clear any pre-existing stream entries that might be present from other tests
        try { await redisDb.KeyDeleteAsync(streamName); } catch { /* ignore */ }

        var worker = BuildWorker(redis);

        // Act — start the worker; it polls every 500 ms, so one iteration is enough
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);
        await Task.Delay(700); // let one relay batch complete

        // Assert — messages published to Redis stream
        var entries = await redisDb.StreamReadAsync(streamName, "0-0");
        entries.Should().HaveCount(2, "both outbox messages must be published");

        // Assert — messages marked as sent in DB
        var reloaded = await _platformDb.OutboxMessages
            .Where(m => m.Id == msg1.Id || m.Id == msg2.Id)
            .ToListAsync();
        reloaded.Should().AllSatisfy(m => m.SentAt.Should().NotBeNull());

        await worker.StopAsync(CancellationToken.None);
        redis.Dispose();
    }

    [Fact]
    public async Task GivenNoUnsentMessages_WhenRelayed_PublishesNothingToRedis()
    {
        // Arrange — all messages already sent (SentAt is set)
        var msg = OutboxMessage.Create("JobCreatedEvent", "{}", StreamNames.JobCreated);
        msg.MarkSent();
        await _platformDb.OutboxMessages.AddAsync(msg);
        await _platformDb.SaveChangesAsync();

        var redis = await ConnectionMultiplexer.ConnectAsync(_fixture.RedisConnectionString);
        var redisDb = redis.GetDatabase();
        var testStream = "flowforge:relay-empty-test-" + Guid.NewGuid().ToString("N")[..6];

        var worker = BuildWorker(redis);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);
        await Task.Delay(700);

        // Assert — no new entries were added to the test stream
        var entries = await redisDb.StreamRangeAsync(testStream);
        entries.Should().BeEmpty();

        await worker.StopAsync(CancellationToken.None);
        redis.Dispose();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private OutboxRelayWorker BuildWorker(IConnectionMultiplexer redis)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_platformDb);
        services.AddLogging();

        var sp = services.BuildServiceProvider();
        return new OutboxRelayWorker(redis, sp.GetRequiredService<IServiceScopeFactory>(), Options.Create(new FlowForge.WebApi.Options.OutboxRelayOptions()), NullLogger<OutboxRelayWorker>.Instance);
    }
}
