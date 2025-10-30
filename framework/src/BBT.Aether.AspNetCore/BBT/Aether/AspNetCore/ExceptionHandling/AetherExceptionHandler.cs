using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.ExceptionHandling;
using BBT.Aether.Results;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using System.Diagnostics;

namespace BBT.Aether.AspNetCore.ExceptionHandling;

public class AetherExceptionHandler : IExceptionHandler
{
    internal const string ErrorFormat = "_aether_error_format";
    private readonly Func<object, Task> _clearCacheHeadersDelegate;
    private readonly ILogger<AetherExceptionHandler> _logger;

    public AetherExceptionHandler(ILogger<AetherExceptionHandler> logger)
    {
        _logger = logger;
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
        var statusCodeFinder = httpContext.RequestServices.GetRequiredService<IHttpExceptionStatusCodeFinder>();
        var exceptionHandlingOptions = httpContext.RequestServices
            .GetRequiredService<IOptions<AetherExceptionHandlingOptions>>().Value;

        var error = errorConverter.ConvertToError(exception, options =>
        {
            options.SendExceptionsDetailsToClients = exceptionHandlingOptions.SendExceptionsDetailsToClients;
            options.SendStackTraceToClients = exceptionHandlingOptions.SendStackTraceToClients;
        });

        var statusCode = statusCodeFinder.GetStatusCodeFromError(error);

        httpContext.Response.Clear();
        httpContext.Response.StatusCode = (int)statusCode;
        httpContext.Response.OnStarting(_clearCacheHeadersDelegate, httpContext.Response);
        httpContext.Response.Headers.Append(ErrorFormat, "true");
        httpContext.Response.Headers.Append("Content-Type", "application/problem+json");

        var problemDetails = CreateProblemDetails(error, statusCode, httpContext, exceptionHandlingOptions);

        await httpContext.Response.WriteAsync(
            JsonSerializer.Serialize(
                problemDetails,
                new JsonSerializerOptions() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }
            ), cancellationToken: cancellationToken);
    }

    private static ProblemDetails CreateProblemDetails(Error error, System.Net.HttpStatusCode statusCode, HttpContext httpContext, AetherExceptionHandlingOptions options)
    {
        var problemDetails = new ProblemDetails
        {
            Status = (int)statusCode,
            Type = $"{options.ErrorTypeBaseUrl.TrimEnd('/')}/{(int)statusCode}/{error.Prefix}/{error.Code.Replace(".", "/").Replace(":", "/")}",
            Title = GetTitleForStatusCode(statusCode),
            Detail = error.Message,
            Instance = httpContext.Request.Path
        };

        // Add custom extensions for Aether-specific error information
        problemDetails.Extensions["errorCode"] = $"{error.Prefix}.{error.Code}";
        problemDetails.Extensions["prefix"] = error.Prefix;
        problemDetails.Extensions["code"] = error.Code;
        problemDetails.Extensions["traceId"] = Activity.Current?.Id ?? string.Empty;

        if (!string.IsNullOrEmpty(error.Detail))
        {
            problemDetails.Extensions["details"] = error.Detail;
        }

        if (!string.IsNullOrEmpty(error.Target))
        {
            problemDetails.Extensions["target"] = error.Target;
        }

        if (error.ValidationErrors != null && error.ValidationErrors.Count > 0)
        {
            problemDetails.Extensions["validationErrors"] = error.ValidationErrors.Select(ve => new
            {
                message = ve.ErrorMessage,
                members = ve.MemberNames?.ToArray()
            }).ToArray();
        }

        return problemDetails;
    }

    private static string GetTitleForStatusCode(System.Net.HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            System.Net.HttpStatusCode.BadRequest => "Bad Request",
            System.Net.HttpStatusCode.Unauthorized => "Unauthorized",
            System.Net.HttpStatusCode.Forbidden => "Forbidden",
            System.Net.HttpStatusCode.NotFound => "Not Found",
            System.Net.HttpStatusCode.Conflict => "Conflict",
            System.Net.HttpStatusCode.InternalServerError => "Internal Server Error",
            System.Net.HttpStatusCode.BadGateway => "Bad Gateway",
            System.Net.HttpStatusCode.ServiceUnavailable => "Service Unavailable",
            System.Net.HttpStatusCode.NotImplemented => "Not Implemented",
            _ => "An error occurred"
        };
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