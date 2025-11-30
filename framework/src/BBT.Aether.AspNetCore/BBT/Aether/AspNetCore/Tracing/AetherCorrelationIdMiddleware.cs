using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using BBT.Aether.Tracing;
using Microsoft.Extensions.Options;

namespace BBT.Aether.AspNetCore.Tracing;

/// <summary>
/// Middleware that handles correlation ID and OpenTelemetry trace context.
/// Sets correlation ID from request headers or generates a new one, and adds trace context to response headers.
/// </summary>
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
            SetResponseHeaders(context, _options, correlationId);
            await next(context);
        }
    }

    private string? GetCorrelationIdFromRequest(HttpContext context)
    {
        var correlationId = context.Request.Headers[_options.HttpHeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            if (!string.IsNullOrWhiteSpace(context.TraceIdentifier))
            {
                correlationId = context.TraceIdentifier;
            }
            else
            {
                correlationId = Guid.NewGuid().ToString("N");
            }

            context.Request.Headers[_options.HttpHeaderName] = correlationId;
        }

        return correlationId;
    }

    private void SetResponseHeaders(
        HttpContext httpContext,
        CorrelationIdOptions options,
        string? correlationId)
    {
        httpContext.Response.OnStarting(() =>
        {
            // Set correlation ID header if enabled
            if (options.SetResponseHeader && !httpContext.Response.Headers.ContainsKey(options.HttpHeaderName) &&
                !string.IsNullOrWhiteSpace(correlationId))
            {
                httpContext.Response.Headers[options.HttpHeaderName] = correlationId;
            }

            // Add OpenTelemetry trace context to response headers
            AddTraceContextToResponse(httpContext);

            return Task.CompletedTask;
        });
    }

    private void AddTraceContextToResponse(HttpContext httpContext)
    {
        var activity = Activity.Current;
        if (activity == null)
        {
            return;
        }

        // Add TraceId (W3C format: 32 hex characters)
        if (activity.TraceId != default)
        {
            httpContext.Response.Headers["X-Trace-Id"] = activity.TraceId.ToString();
        }

        // Add SpanId (W3C format: 16 hex characters)
        if (activity.SpanId != default)
        {
            httpContext.Response.Headers["X-Span-Id"] = activity.SpanId.ToString();
        }

        // Add TraceState if present (optional W3C header)
        if (!string.IsNullOrEmpty(activity.TraceStateString))
        {
            httpContext.Response.Headers["X-Trace-State"] = activity.TraceStateString;
        }

        // Add W3C Trace Context standard header (traceparent)
        // Format: version-traceId-spanId-flags
        var traceParent = $"00-{activity.TraceId}-{activity.SpanId}-{(activity.ActivityTraceFlags.HasFlag(ActivityTraceFlags.Recorded) ? "01" : "00")}";
        httpContext.Response.Headers["traceparent"] = traceParent;
    }
}