using FlowForge.Infrastructure.Persistence.Jobs;
using FlowForge.Infrastructure.Persistence.Platform;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace FlowForge.Integration.Tests.Infrastructure;

/// <summary>
/// Starts PostgreSQL and Redis containers once per test collection.
/// Applies EF migrations on first use.
/// </summary>
public class SharedContainersFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pgContainer = new PostgreSqlBuilder().Build();
    private readonly RedisContainer _redisContainer = new RedisBuilder().Build();

    public string PlatformConnectionString { get; private set; } = null!;
    public string JobsConnectionString { get; private set; } = null!;
    public string RedisConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_pgContainer.StartAsync(), _redisContainer.StartAsync());

        PlatformConnectionString = _pgContainer.GetConnectionString();
        RedisConnectionString = _redisContainer.GetConnectionString();

        // Create a separate jobs database on the same PostgreSQL instance
        await _pgContainer.ExecScriptAsync("CREATE DATABASE jobs_test;");
        var jobsBuilder = new NpgsqlConnectionStringBuilder(PlatformConnectionString) { Database = "jobs_test" };
        JobsConnectionString = jobsBuilder.ConnectionString;

        await ApplyMigrationsAsync();
    }

    public async Task DisposeAsync()
    {
        await _pgContainer.DisposeAsync();
        await _redisContainer.DisposeAsync();
    }

    public PlatformDbContext CreatePlatformDbContext()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(PlatformConnectionString)
            .Options;
        return new PlatformDbContext(options);
    }

    public JobsDbContext CreateJobsDbContext()
    {
        var options = new DbContextOptionsBuilder<JobsDbContext>()
            .UseNpgsql(JobsConnectionString)
            .Options;
        return new JobsDbContext(options);
    }

    private async Task ApplyMigrationsAsync()
    {
        await using var platformDb = CreatePlatformDbContext();
        await platformDb.Database.MigrateAsync();

        await using var jobsDb = CreateJobsDbContext();
        await jobsDb.Database.MigrateAsync();
    }
}

[CollectionDefinition("Containers")]
public class ContainersCollectionDefinition : ICollectionFixture<SharedContainersFixture> { }
