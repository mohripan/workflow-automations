using FlowForge.Infrastructure.Messaging.Redis;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Caching;
using FlowForge.Infrastructure.Persistence.Platform;
using FlowForge.Infrastructure.Persistence.Jobs;
using FlowForge.Infrastructure.Repositories;
using FlowForge.Domain.Repositories;
using FlowForge.Infrastructure.MultiDb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace FlowForge.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        // Redis
        var redisConnString = config.GetSection("Redis:ConnectionString").Value 
            ?? throw new ArgumentNullException("Redis:ConnectionString");
        services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisConnString));
        services.AddSingleton<IMessagePublisher, RedisStreamPublisher>();
        services.AddSingleton<IMessageConsumer, RedisStreamConsumer>();
        services.AddSingleton<IRedisService, RedisService>();

        // Platform DB
        services.AddDbContext<PlatformDbContext>(options =>
            options.UseNpgsql(config.GetConnectionString("DefaultConnection")));

        services.AddScoped<IAutomationRepository, AutomationRepository>();
        services.AddScoped<IHostGroupRepository, HostGroupRepository>();
        services.AddScoped<IWorkflowHostRepository, WorkflowHostRepository>();

        // Multi-DB Job Repositories
        var jobConnections = config.GetSection("JobConnections").Get<Dictionary<string, JobConnectionConfig>>() ?? [];

        foreach (var (connectionId, connConfig) in jobConnections)
        {
            services.AddKeyedScoped<IJobRepository>(connectionId, (sp, key) =>
            {
                var optionsBuilder = new DbContextOptionsBuilder<JobsDbContext>();
                optionsBuilder.UseNpgsql(connConfig.ConnectionString);
                return new JobRepository(new JobsDbContext(optionsBuilder.Options));
            });
        }

        return services;
    }
}
