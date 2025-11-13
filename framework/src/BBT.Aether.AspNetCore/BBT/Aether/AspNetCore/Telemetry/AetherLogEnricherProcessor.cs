using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace BBT.Aether.AspNetCore.Telemetry;

public sealed class AetherLogEnricherProcessor(
    AetherTelemetryOptions options,
    List<Regex> excludedPatterns,
    IHttpContextAccessor? httpContextAccessor)
    : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord logRecord)
    {
        if (logRecord == null)
            return;

        var httpContext = httpContextAccessor?.HttpContext;
        if (httpContext == null)
            return;

        // Check if path should be excluded
        var path = httpContext.Request.Path.Value;
        if (!string.IsNullOrEmpty(path) && IsExcluded(path))
            return;

        // Add HTTP headers as attributes
        foreach (var header in options.Logging.Enrichers.Headers)
        {
            try
            {
                if (httpContext.Request.Headers.TryGetValue(header, out var value))
                {
                    var headerKey = $"http.request.header.{header.ToLowerInvariant()}";
                    logRecord.Attributes ??= new List<KeyValuePair<string, object?>>();
                    
                    // Add as attribute
                    if (logRecord.Attributes is List<KeyValuePair<string, object?>> attributesList)
                    {
                        attributesList.Add(new KeyValuePair<string, object?>(headerKey, value.ToString()));
                    }
                }
            }
            catch
            {
                // Skip problematic headers
            }
        }

        // Add custom attributes
        foreach (var attr in options.Logging.Enrichers.CustomAttributes)
        {
            try
            {
                logRecord.Attributes ??= new List<KeyValuePair<string, object?>>();
                
                if (logRecord.Attributes is List<KeyValuePair<string, object?>> attributesList)
                {
                    attributesList.Add(new KeyValuePair<string, object?>(attr.Key, attr.Value));
                }
            }
            catch
            {
                // Skip problematic attributes
            }
        }

        base.OnEnd(logRecord);
    }

    private bool IsExcluded(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        return excludedPatterns.Any(pattern =>
        {
            try
            {
                return pattern.IsMatch(value);
            }
            catch
            {
                return false;
            }
        });
    }
}

