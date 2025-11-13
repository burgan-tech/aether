using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using BBT.Aether.Events;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Aether.AspNetCore.Events;

/// <summary>
/// Abstract base class for Dapr subscription discovery endpoint.
/// Returns subscription configuration for all registered event handlers.
/// Developers should inherit from this class and create their own controller action with custom route.
/// </summary>
public abstract class DaprDiscoveryController(IDistributedEventInvokerRegistry invokerRegistry) : ControllerBase
{
    protected readonly IDistributedEventInvokerRegistry InvokerRegistry = invokerRegistry;

    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Returns Dapr subscription configuration for all registered event handlers.
    /// This method is called by Dapr runtime to discover subscriptions.
    /// Dapr expects this endpoint at /dapr/subscribe by default.
    /// Developers should call this method from their controller action.
    /// </summary>
    /// <returns>JSON result containing subscription configuration</returns>
    protected virtual IActionResult GetSubscriptions()
    {
        var subscriptions = InvokerRegistry.All
            .Select(invoker => new
            {
                pubsubname = invoker.PubSubName,
                topic = invoker.Topic,
                route = $"/events/{invoker.Name}/v{invoker.Version}"
            })
            .ToList();

        return new JsonResult(subscriptions, JsonOptions);
    }
}

