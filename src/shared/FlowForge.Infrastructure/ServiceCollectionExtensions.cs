using FlowForge.Infrastructure.Audit;
using FlowForge.Infrastructure.Auth;
using Microsoft.AspNetCore.Authentication;
using FlowForge.Infrastructure.Messaging;
using FlowForge.Infrastructure.Messaging.Redis;
using FlowForge.Infrastructure.Messaging.Dapr;
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
        services.AddRedisCaching(config);
        services.AddMessaging(config);
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

    /// <summary>
    /// Registers the messaging provider based on <c>Messaging:Provider</c> config.
    /// Supported providers: "redis" (default), "dapr".
    /// </summary>
    public static IServiceCollection AddMessaging(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<MessagingOptions>(config.GetSection(MessagingOptions.SectionName));

        var provider = config.GetSection(MessagingOptions.SectionName)
            .GetValue<string>("Provider") ?? "redis";

        return provider.ToLowerInvariant() switch
        {
            "dapr" => services.AddDaprMessaging(config),
            _ => services.AddRedisMessaging(),
        };
    }

    /// <summary>
    /// Registers Redis-based messaging (publisher, consumer, DLQ, bootstrapper).
    /// Redis connection is registered by <see cref="AddRedisCaching"/> and shared.
    /// </summary>
    public static IServiceCollection AddRedisMessaging(this IServiceCollection services)
    {
        services.AddSingleton<IMessagePublisher, RedisStreamPublisher>();
        services.AddSingleton<IMessageConsumer, RedisStreamConsumer>();
        services.AddSingleton<IStreamBootstrapper, RedisStreamBootstrapper>();
        services.AddSingleton<IMessagingInfrastructure, RedisMessagingInfrastructure>();
        services.AddSingleton<IDlqWriter, DlqWriter>();
        services.AddSingleton<IDlqReader, RedisDlqReader>();
        return services;
    }

    /// <summary>
    /// Registers Redis caching (<see cref="IRedisService"/>) and the underlying
    /// <see cref="IConnectionMultiplexer"/>. This is always registered regardless
    /// of the messaging provider — Redis is used for caching in both modes.
    /// </summary>
    public static IServiceCollection AddRedisCaching(this IServiceCollection services, IConfiguration config)
    {
        // Only register once — check if IConnectionMultiplexer is already registered
        if (services.Any(d => d.ServiceType == typeof(IConnectionMultiplexer)))
            return services;

        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var connStr = sp.GetRequiredService<IConfiguration>().GetSection("Redis:ConnectionString").Value
                ?? throw new ArgumentNullException("Redis:ConnectionString");
            return ConnectionMultiplexer.Connect(connStr);
        });
        services.AddSingleton<IRedisService, RedisService>();
        return services;
    }

    /// <summary>
    /// Legacy method that registers both Redis caching and Redis messaging.
    /// Kept for backward compatibility.
    /// </summary>
    public static IServiceCollection AddRedis(this IServiceCollection services)
    {
        // Register Redis connection + caching
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var connStr = sp.GetRequiredService<IConfiguration>().GetSection("Redis:ConnectionString").Value
                ?? throw new ArgumentNullException("Redis:ConnectionString");
            return ConnectionMultiplexer.Connect(connStr);
        });
        services.AddSingleton<IRedisService, RedisService>();

        // Register Redis messaging
        services.AddRedisMessaging();

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
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();

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

    /// <summary>
    /// Registers <see cref="ICurrentUserService"/> and <see cref="IAuditLogger"/>.
    /// Call <c>AddHttpContextAccessor()</c> before this in the WebApi host.
    /// </summary>
    public static IServiceCollection AddAuditLogging(this IServiceCollection services)
    {
        services.AddScoped<ICurrentUserService, HttpContextCurrentUserService>();
        services.AddScoped<IAuditLogger, AuditLogger>();
        return services;
    }

    /// <summary>
    /// Registers <see cref="KeycloakClientOptions"/> and <see cref="ClientCredentialsHandler"/>
    /// so callers can attach the handler to any named or typed HttpClient.
    /// </summary>
    public static IServiceCollection AddKeycloakClientCredentials(
        this IServiceCollection services, IConfiguration config)
    {
        services.Configure<KeycloakClientOptions>(
            config.GetSection(KeycloakClientOptions.SectionName));
        services.AddTransient<ClientCredentialsHandler>();
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
