using System.Threading.Tasks;

namespace BBT.Aether.Services;

/// <summary>
/// Represents a service for external communication in the domain
/// </summary>
public interface IExternalService
{
    /// <summary>
    /// Invokes an external service method asynchronously
    /// </summary>
    /// <typeparam name="TRequest">The type of the request data</typeparam>
    /// <typeparam name="TResponse">The type of the expected response</typeparam>
    /// <param name="serviceName">The name of the service to invoke</param>
    /// <param name="operation">The operation to invoke</param>
    /// <param name="request">The request data to send</param>
    /// <returns>The response from the external service, or default if the call fails</returns>
    Task<TResponse?> InvokeAsync<TRequest, TResponse>(
        string serviceName,
        string operation,
        TRequest request);
}