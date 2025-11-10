using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using BBT.Aether.Events;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Aether.AspNetCore.Events;

/// <summary>
/// Dapr subscription discovery endpoint.
/// Returns subscription configuration for all registered event handlers.
/// </summary>
[ApiController]
[Route("dapr")]
public sealed class DaprDiscoveryController(IDistributedEventInvokerRegistry invokerRegistry) : ControllerBase
{
    private readonly static JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Returns Dapr subscription configuration for all registered event handlers.
    /// This endpoint is called by Dapr runtime to discover subscriptions.
    /// Dapr expects this endpoint at /dapr/subscribe.
    /// </summary>
    [HttpGet("subscribe", Order =int.MinValue)]
    [ApiExplorerSettings(IgnoreApi = true)]
    public IActionResult Subscribe()
    {
        var subscriptions = invokerRegistry.All
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

