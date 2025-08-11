using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Aether.ExceptionHandling;
using BBT.Aether.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
        LogException(context, out var serviceErrorInfo);
        if (!context.HttpContext.Response.HasStarted)
        {
            context.HttpContext.Response.Headers.Append(AetherExceptionHandler.ErrorFormat, "true");
            context.HttpContext.Response.StatusCode = (int)context
                .GetRequiredService<IHttpExceptionStatusCodeFinder>()
                .GetStatusCode(context.HttpContext, context.Exception);
        }

        context.Result = new ObjectResult(new ServiceErrorResponse(serviceErrorInfo));

        context.ExceptionHandled = true;
        return Task.CompletedTask;
    }
    
    protected virtual void LogException(ExceptionContext context, out ServiceErrorInfo serviceErrorInfo)
    {
        var exceptionHandlingOptions = context.GetRequiredService<IOptions<AetherExceptionHandlingOptions>>().Value;
        var exceptionToErrorInfoConverter = context.GetRequiredService<IExceptionToErrorInfoConverter>();
        serviceErrorInfo = exceptionToErrorInfoConverter.Convert(context.Exception, options =>
        {
            options.SendExceptionsDetailsToClients = exceptionHandlingOptions.SendExceptionsDetailsToClients;
            options.SendStackTraceToClients = exceptionHandlingOptions.SendStackTraceToClients;
        });

        var remoteServiceErrorInfoBuilder = new StringBuilder();
        remoteServiceErrorInfoBuilder.AppendLine($"---------- {nameof(ServiceErrorInfo)} ----------");
        remoteServiceErrorInfoBuilder.AppendLine(JsonSerializer.Serialize(serviceErrorInfo, new JsonSerializerOptions()
        {
            WriteIndented = true
        }));

        var logger = context.GetService<ILogger<AetherExceptionFilter>>(NullLogger<AetherExceptionFilter>.Instance)!;
        var logLevel = context.Exception.GetLogLevel();
        logger.LogWithLevel(logLevel, remoteServiceErrorInfoBuilder.ToString());
        logger.LogException(context.Exception, logLevel);
    }
}