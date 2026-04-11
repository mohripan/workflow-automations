using Dapr.Client;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.DeadLetter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlowForge.Infrastructure.Messaging.Dapr;

/// <summary>
/// DI registration for Dapr-based messaging (publisher, DLQ, infrastructure).
/// </summary>
public static class DaprMessagingExtensions
{
    public static IServiceCollection AddDaprMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<DaprOptions>(configuration.GetSection(DaprOptions.SectionName));

        services.AddSingleton(new DaprClientBuilder().Build());
        services.AddSingleton<IMessagePublisher, DaprMessagePublisher>();
        services.AddSingleton<IDlqWriter, DaprDlqWriter>();
        services.AddSingleton<IDlqReader, DaprDlqReader>();
        services.AddSingleton<IMessagingInfrastructure, DaprMessagingInfrastructure>();

        return services;
    }
}
