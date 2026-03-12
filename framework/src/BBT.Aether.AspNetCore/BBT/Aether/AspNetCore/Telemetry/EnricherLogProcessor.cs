using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace BBT.Aether.AspNetCore.Telemetry;

/// <summary>
/// Adds Telemetry:Logging:Enrichers (CustomAttributes + Headers) as attributes to every log record,
/// so all application logs can be queried with the same enrich fields. Skips when the record already
/// has RequestHeaders/ResponseHeaders (HTTP body middleware log) to avoid duplicate.
/// </summary>
public sealed class EnricherLogProcessor(
    AetherTelemetryOptions options,
    IHttpContextAccessor? httpContextAccessor)
    : BaseProcessor<LogRecord>
{
    private readonly HashSet<string> _sensitiveHeaderNames = BuildSensitiveHeaderNames(options);

    public override void OnEnd(LogRecord record)
    {
        if (record == null)
            return;

        if (HasBodyOrHeaderEnrichment(record))
            return;

        var enricherAttrs = new List<KeyValuePair<string, object?>>();

        if (options.Logging?.Enrichers?.CustomAttributes != null)
        {
            foreach (var kv in options.Logging.Enrichers.CustomAttributes)
            {
                enricherAttrs.Add(new KeyValuePair<string, object?>(kv.Key, kv.Value));
            }
        }

        var httpContext = httpContextAccessor?.HttpContext;
        if (httpContext != null && options.Logging?.Enrichers?.Headers is { Count: > 0 } headerNames)
        {
            foreach (var headerName in headerNames)
            {
                if (string.IsNullOrWhiteSpace(headerName))
                    continue;
                var key = headerName.Trim();
                var requestKey = $"RequestHeader.{NormalizeHeaderKey(key)}";
                var responseKey = $"ResponseHeader.{NormalizeHeaderKey(key)}";
                var isSensitive = _sensitiveHeaderNames.Contains(key);

                if (httpContext.Request.Headers.TryGetValue(key, out var reqVal))
                    enricherAttrs.Add(new KeyValuePair<string, object?>(requestKey, isSensitive ? "***REDACTED***" : reqVal.ToString()));
                if (httpContext.Response.Headers.TryGetValue(key, out var resVal))
                    enricherAttrs.Add(new KeyValuePair<string, object?>(responseKey, isSensitive ? "***REDACTED***" : resVal.ToString()));
            }
        }

        if (enricherAttrs.Count == 0)
            return;

        var existing = record.Attributes ?? Array.Empty<KeyValuePair<string, object?>>();
        var merged = new List<KeyValuePair<string, object?>>(existing.Count + enricherAttrs.Count);
        foreach (var kv in existing)
            merged.Add(kv);
        foreach (var kv in enricherAttrs)
            merged.Add(kv);
        record.Attributes = merged;
    }

    private static HashSet<string> BuildSensitiveHeaderNames(AetherTelemetryOptions options)
    {
        var body = options.Logging?.Body;
        var additional = body?.AdditionalSensitiveHeaderNames ?? [];
        return new HashSet<string>(
            HttpBodyLoggingOptions.DefaultSensitiveHeaderNames.Concat(additional),
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasBodyOrHeaderEnrichment(LogRecord record)
    {
        var attrs = record.Attributes;
        if (attrs == null)
            return false;
        foreach (var kv in attrs)
        {
            if (kv.Key == "RequestHeaders" || kv.Key == "ResponseHeaders" || kv.Key == "RequestBody" || kv.Key == "ResponseBody")
                return true;
        }
        return false;
    }

    private static string NormalizeHeaderKey(string key)
        => key.Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
}
