using FlowForge.Infrastructure.Messaging.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowForge.Infrastructure.Messaging.Dapr;

/// <summary>
/// Extension methods for mapping Dapr pub/sub subscription endpoints.
/// Each event type gets a POST endpoint that Dapr pushes messages to.
/// The endpoint resolves the appropriate <see cref="IEventHandler{TEvent}"/>
/// and delegates the message handling.
/// </summary>
public static class DaprSubscriptionEndpoints
{
    /// <summary>
    /// Maps a Dapr subscription endpoint for a specific event type.
    /// The endpoint will be POST /dapr/{topicName} and will resolve
    /// <see cref="IEventHandler{TEvent}"/> from DI to handle the event.
    /// </summary>
    public static RouteHandlerBuilder MapDaprSubscription<TEvent>(
        this IEndpointRouteBuilder endpoints,
        string topicName) where TEvent : class
    {
        return endpoints.MapPost($"/dapr/{topicName}", async (
            TEvent @event,
            IServiceProvider sp,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("FlowForge.Dapr.Subscriptions");
            var handler = sp.GetRequiredService<IEventHandler<TEvent>>();

            try
            {
                await handler.HandleAsync(@event, ct);
                return Results.Ok();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error handling Dapr subscription for {EventType} on topic {Topic}",
                    typeof(TEvent).Name, topicName);

                // Return 200 to prevent Dapr from retrying — DLQ handling is done inside the handler
                return Results.Ok();
            }
        }).AllowAnonymous(); // Dapr sidecar calls are process-local, no auth needed
    }

    /// <summary>
    /// Maps all standard FlowForge Dapr subscription endpoints.
    /// Call this from each service's Program.cs, passing the event types
    /// the service subscribes to.
    /// </summary>
    public static IEndpointRouteBuilder MapDaprSubscriptions(
        this IEndpointRouteBuilder endpoints,
        IReadOnlyList<DaprTopicMapping> mappings)
    {
        foreach (var mapping in mappings)
        {
            mapping.MapEndpoint(endpoints);
        }
        return endpoints;
    }
}

/// <summary>
/// Describes a mapping between a Dapr topic and its event handler type.
/// Used to dynamically register subscription endpoints per service.
/// </summary>
public abstract class DaprTopicMapping
{
    public abstract string TopicName { get; }
    public abstract void MapEndpoint(IEndpointRouteBuilder endpoints);
}

/// <summary>
/// Typed implementation of <see cref="DaprTopicMapping"/> for a specific event type.
/// </summary>
public sealed class DaprTopicMapping<TEvent> : DaprTopicMapping where TEvent : class
{
    private readonly string _topicName;

    public DaprTopicMapping(string topicName) => _topicName = topicName;

    public override string TopicName => _topicName;

    public override void MapEndpoint(IEndpointRouteBuilder endpoints) =>
        endpoints.MapDaprSubscription<TEvent>(_topicName);
}
