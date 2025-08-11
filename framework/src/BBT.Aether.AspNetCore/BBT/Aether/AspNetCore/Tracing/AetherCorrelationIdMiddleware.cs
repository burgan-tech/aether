using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using BBT.Aether.Tracing;
using Microsoft.Extensions.Options;

namespace BBT.Aether.AspNetCore.Tracing;

public sealed class AetherCorrelationIdMiddleware(
    IOptions<CorrelationIdOptions> options,
    ICorrelationIdProvider correlationIdProvider)
    : IMiddleware
{
    private readonly CorrelationIdOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var correlationId = GetCorrelationIdFromRequest(context);
        using (correlationIdProvider.Change(correlationId))
        {
            CheckAndSetCorrelationIdOnResponse(context, _options, correlationId);
            await next(context);
        }
    }

    private string? GetCorrelationIdFromRequest(HttpContext context)
    {
        var correlationId = context.Request.Headers[_options.HttpHeaderName];
        if (correlationId.IsNullOrEmpty())
        {
            if (context.TraceIdentifier != null)
            {
                correlationId = context.TraceIdentifier;
            }

            if (correlationId.IsNullOrEmpty())
            {
                correlationId = Guid.NewGuid().ToString("N");
            }

            context.Request.Headers[_options.HttpHeaderName] = correlationId;
        }

        return correlationId;
    }

    private void CheckAndSetCorrelationIdOnResponse(
        HttpContext httpContext,
        CorrelationIdOptions options,
        string? correlationId)
    {
        httpContext.Response.OnStarting(() =>
        {
            if (options.SetResponseHeader && !httpContext.Response.Headers.ContainsKey(options.HttpHeaderName) &&
                !string.IsNullOrWhiteSpace(correlationId))
            {
                httpContext.Response.Headers[options.HttpHeaderName] = correlationId;
            }

            return Task.CompletedTask;
        });
    }
}