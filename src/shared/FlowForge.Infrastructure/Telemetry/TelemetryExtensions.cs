using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace FlowForge.Infrastructure.Telemetry;

/// <summary>
/// Shared ActivitySource used by RedisStreamPublisher and RedisStreamConsumer
/// for "publish {stream}" and "consume {stream}" spans.
/// </summary>
public static class FlowForgeActivitySources
{
    public const string MessagingName = "FlowForge.Messaging";
    public static readonly ActivitySource Messaging = new(MessagingName);
}

public static class TelemetryExtensions
{
    /// <summary>
    /// Registers OpenTelemetry tracing for a FlowForge service.
    /// Adds the FlowForge.Messaging source (publish/consume spans) and a
    /// service-specific source (FlowForge.{serviceName}) for business-logic spans.
    /// Configures the OTLP exporter when OpenTelemetry:OtlpEndpoint is set in config.
    /// </summary>
    public static IServiceCollection AddFlowForgeTelemetry(
        this IServiceCollection services,
        IConfiguration config,
        string serviceName)
    {
        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(FlowForgeActivitySources.MessagingName)
                    .AddSource($"FlowForge.{serviceName}");

                var endpoint = config["OpenTelemetry:OtlpEndpoint"];
                if (!string.IsNullOrEmpty(endpoint))
                    tracing.AddOtlpExporter(o => o.Endpoint = new Uri(endpoint));
            });

        return services;
    }
}
