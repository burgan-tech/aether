using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Aether.ExceptionHandling;
using BBT.Aether.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BBT.Aether.AspNetCore.ExceptionHandling;

public class AetherExceptionFilter(IProblemDetailsFactory problemDetailsFactory) : IAsyncExceptionFilter
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
        
        return true;
    }

    protected virtual Task HandleAndWrapException(ExceptionContext context)
    {
        LogException(context, out var error);
        
        var problemDetails = problemDetailsFactory.CreateProblemDetails(error, context.HttpContext);
        
        if (!context.HttpContext.Response.HasStarted)
        {
            context.HttpContext.Response.Headers.Append(AetherExceptionHandler.ErrorFormat, "true");
            context.HttpContext.Response.StatusCode = problemDetails.Status ?? 500;
        }

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

}