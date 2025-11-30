using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.ExceptionHandling;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace BBT.Aether.AspNetCore.ExceptionHandling;

public class AetherExceptionHandler : IExceptionHandler
{
    internal const string ErrorFormat = "_aether_error_format";
    private readonly Func<object, Task> _clearCacheHeadersDelegate;
    private readonly ILogger<AetherExceptionHandler> _logger;
    private readonly IProblemDetailsFactory _problemDetailsFactory;

    public AetherExceptionHandler(
        ILogger<AetherExceptionHandler> logger,
        IProblemDetailsFactory problemDetailsFactory)
    {
        _logger = logger;
        _problemDetailsFactory = problemDetailsFactory;
        _clearCacheHeadersDelegate = ClearCacheHeaders;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception,
        CancellationToken cancellationToken)
    {
        await HandleAndWrapException(httpContext, exception, cancellationToken);

        return true;
    }

    private async Task HandleAndWrapException(HttpContext httpContext, Exception exception,
        CancellationToken cancellationToken)
    {
        _logger.LogException(exception);
        var errorConverter = httpContext.RequestServices.GetRequiredService<IExceptionToErrorInfoConverter>();
        var exceptionHandlingOptions = httpContext.RequestServices
            .GetRequiredService<IOptions<AetherExceptionHandlingOptions>>().Value;

        var error = errorConverter.ConvertToError(exception, options =>
        {
            options.SendExceptionsDetailsToClients = exceptionHandlingOptions.SendExceptionsDetailsToClients;
            options.SendStackTraceToClients = exceptionHandlingOptions.SendStackTraceToClients;
        });

        var problemDetails = _problemDetailsFactory.CreateProblemDetails(error, httpContext);

        httpContext.Response.Clear();
        httpContext.Response.StatusCode = problemDetails.Status ?? 500;
        httpContext.Response.OnStarting(_clearCacheHeadersDelegate, httpContext.Response);
        httpContext.Response.Headers.Append(ErrorFormat, "true");
        httpContext.Response.Headers.Append("Content-Type", "application/problem+json");

        await httpContext.Response.WriteAsync(
            JsonSerializer.Serialize(
                problemDetails,
                new JsonSerializerOptions() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }
            ), cancellationToken: cancellationToken);
    }

    private Task ClearCacheHeaders(object state)
    {
        var response = (HttpResponse)state;

        response.Headers[HeaderNames.CacheControl] = "no-cache";
        response.Headers[HeaderNames.Pragma] = "no-cache";
        response.Headers[HeaderNames.Expires] = "-1";
        response.Headers.Remove(HeaderNames.ETag);

        return Task.CompletedTask;
    }
}