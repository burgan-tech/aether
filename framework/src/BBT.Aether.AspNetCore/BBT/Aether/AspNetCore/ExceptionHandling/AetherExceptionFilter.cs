using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Aether.ExceptionHandling;
using BBT.Aether.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace BBT.Aether.AspNetCore.ExceptionHandling;

public class AetherExceptionFilter : IAsyncExceptionFilter
{
    public virtual async Task OnExceptionAsync(ExceptionContext context)
    {
        if (!ShouldHandleException(context))
        {
            LogException(context, out _);
            return;
        }

        await HandleAndWrapException(context);
    }

    protected virtual bool ShouldHandleException(ExceptionContext context)
    {
        if (context.ExceptionHandled)
        {
            return false;
        }
        
        return false;
    }

    protected virtual Task HandleAndWrapException(ExceptionContext context)
    {
        LogException(context, out var error);
        if (!context.HttpContext.Response.HasStarted)
        {
            context.HttpContext.Response.Headers.Append(AetherExceptionHandler.ErrorFormat, "true");
            var statusCodeFinder = context.GetRequiredService<IHttpExceptionStatusCodeFinder>();
            var statusCode = statusCodeFinder.GetStatusCodeFromError(error);
            context.HttpContext.Response.StatusCode = (int)statusCode;
        }

        var exceptionHandlingOptions = context.GetRequiredService<IOptions<AetherExceptionHandlingOptions>>().Value;
        var problemDetails = CreateProblemDetails(error, context.HttpContext, exceptionHandlingOptions);
        context.Result = new ObjectResult(problemDetails);

        context.ExceptionHandled = true;
        return Task.CompletedTask;
    }
    
    protected virtual void LogException(ExceptionContext context, out Error error)
    {
        var exceptionHandlingOptions = context.GetRequiredService<IOptions<AetherExceptionHandlingOptions>>().Value;
        var exceptionToErrorConverter = context.GetRequiredService<IExceptionToErrorInfoConverter>();
        error = exceptionToErrorConverter.ConvertToError(context.Exception, options =>
        {
            options.SendExceptionsDetailsToClients = exceptionHandlingOptions.SendExceptionsDetailsToClients;
            options.SendStackTraceToClients = exceptionHandlingOptions.SendStackTraceToClients;
        });

        var errorBuilder = new StringBuilder();
        errorBuilder.AppendLine($"---------- {nameof(Error)} ----------");
        errorBuilder.AppendLine(JsonSerializer.Serialize(error, new JsonSerializerOptions()
        {
            WriteIndented = true
        }));

        var logger = context.GetService<ILogger<AetherExceptionFilter>>(NullLogger<AetherExceptionFilter>.Instance)!;
        var logLevel = context.Exception.GetLogLevel();
        logger.LogWithLevel(logLevel, errorBuilder.ToString());
        logger.LogException(context.Exception, logLevel);
    }

    private static ProblemDetails CreateProblemDetails(Error error, HttpContext httpContext, AetherExceptionHandlingOptions options)
    {
        var statusCodeFinder = httpContext.RequestServices.GetRequiredService<IHttpExceptionStatusCodeFinder>();
        var statusCode = statusCodeFinder.GetStatusCodeFromError(error);

        var problemDetails = new ProblemDetails
        {
            Status = (int)statusCode,
            Type = $"{options.ErrorTypeBaseUrl.TrimEnd('/')}/{(int)statusCode}",
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
}