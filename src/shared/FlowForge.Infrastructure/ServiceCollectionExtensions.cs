using FlowForge.Infrastructure.Messaging.Redis;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.DeadLetter;
using FlowForge.Infrastructure.Messaging.Outbox;
using FlowForge.Infrastructure.Caching;
using FlowForge.Infrastructure.Encryption;
using FlowForge.Infrastructure.Telemetry;
using FlowForge.Infrastructure.Persistence.Platform;
using FlowForge.Infrastructure.Persistence.Jobs;
using FlowForge.Infrastructure.Repositories;
using FlowForge.Domain.Repositories;
using FlowForge.Domain.Tasks;
using FlowForge.Domain.Triggers;
using FlowForge.Infrastructure.MultiDb;
using FlowForge.Infrastructure.Tasks;
using FlowForge.Infrastructure.Tasks.Descriptors;
using FlowForge.Infrastructure.Triggers;
using FlowForge.Infrastructure.Triggers.Descriptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace FlowForge.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config, string? serviceName = null)
    {
        services.AddRedis(config);
        services.AddPersistence(config);
        services.AddTriggerTypeRegistry();
        services.AddTaskTypeRegistry();
        services.AddEncryption();
        if (serviceName is not null)
            services.AddFlowForgeTelemetry(config, serviceName);
        return services;
    }

    public static IServiceCollection AddEncryption(this IServiceCollection services)
    {
        services.AddSingleton<IEncryptionService, AesEncryptionService>();
        return services;
    }

    public static IServiceCollection AddRedis(this IServiceCollection services, IConfiguration config)
    {
        var redisConnString = config.GetSection("Redis:ConnectionString").Value
            ?? throw new ArgumentNullException("Redis:ConnectionString");
        services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisConnString));
        services.AddSingleton<IMessagePublisher, RedisStreamPublisher>();
        services.AddSingleton<IMessageConsumer, RedisStreamConsumer>();
        services.AddSingleton<IStreamBootstrapper, RedisStreamBootstrapper>();
        services.AddSingleton<IRedisService, RedisService>();
        services.AddSingleton<IDlqWriter, DlqWriter>();
        return services;
    }

    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration config)
    {
        // Platform DB
        services.AddDbContext<PlatformDbContext>(options =>
            options.UseNpgsql(config.GetConnectionString("DefaultConnection")));

        services.AddScoped<IOutboxWriter, OutboxWriter>();
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

    private static IServiceCollection AddTaskTypeRegistry(this IServiceCollection services)
    {
        services.AddSingleton<ITaskTypeRegistry>(sp =>
        {
            var registry = new TaskTypeRegistry();
            registry.Register(new HttpRequestTaskDescriptor());
            registry.Register(new RunScriptTaskDescriptor());
            return registry;
        });
        return services;
    }

    private static IServiceCollection AddTriggerTypeRegistry(this IServiceCollection services)
    {
        services.AddSingleton<ITriggerTypeRegistry>(sp =>
        {
            var registry = new TriggerTypeRegistry();
            registry.Register(new ScheduleTriggerDescriptor());
            registry.Register(new SqlTriggerDescriptor());
            registry.Register(new JobCompletedTriggerDescriptor());
            registry.Register(new WebhookTriggerDescriptor());
            registry.Register(new CustomScriptTriggerDescriptor());
            return registry;
        });
        return services;
    }
}
