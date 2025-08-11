using System;
using System.Net.Http;
using System.Threading.Tasks;
using Dapr.Client;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.Services.Dapr;

/// <summary>
/// Base implementation of external service communication using Dapr
/// </summary>
public abstract class DaprExternalService(DaprClient daprClient, ILogger logger) : IExternalService
{
    protected readonly DaprClient DaprClient = daprClient;
    protected readonly ILogger Logger = logger;

    /// <inheritdoc />
    public async Task<TResponse?> InvokeAsync<TRequest, TResponse>(
        string serviceName,
        string operation,
        TRequest request)
    {
        try
        {
            var response = await DaprClient.InvokeMethodAsync<TRequest, TResponse>(
                HttpMethod.Post,
                serviceName,
                operation,
                request);

            Logger.LogInformation("Successfully called external service {ServiceName}/{Operation}", 
                serviceName, operation);
            return response;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error calling external service {ServiceName}/{Operation}: {Message}", 
                serviceName, operation, ex.Message);
            return default;
        }
    }
}
