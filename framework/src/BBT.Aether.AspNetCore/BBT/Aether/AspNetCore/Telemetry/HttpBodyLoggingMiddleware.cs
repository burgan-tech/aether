using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Aether.AspNetCore.Telemetry;

/// <summary>
/// Middleware that captures HTTP request/response bodies and headers and logs them via ILogger only (no trace/span tags).
/// Configuration is read from <see cref="AetherTelemetryOptions.Logging"/>.<see cref="AetherLoggingOptions.Body"/>.
/// Path exclusion uses <see cref="AetherLoggingOptions.ExcludedPaths"/>.
/// </summary>
public sealed class HttpBodyLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<HttpBodyLoggingMiddleware> _logger;
    private readonly HttpBodyLoggingOptions _bodyOptions;
    private readonly LoggingEnricherOptions _enricherOptions;
    private readonly List<Regex> _excludedPatterns;
    private readonly HashSet<string> _sensitiveJsonFields;
    private readonly HashSet<string> _sensitiveHeaderNames;

    public HttpBodyLoggingMiddleware(
        RequestDelegate next,
        ILogger<HttpBodyLoggingMiddleware> logger,
        IOptions<AetherTelemetryOptions> options)
    {
        _next = next;
        _logger = logger;
        var opts = options.Value;
        _bodyOptions = opts.Logging?.Body ?? new HttpBodyLoggingOptions();
        _enricherOptions = opts.Logging?.Enrichers ?? new LoggingEnricherOptions();
        _excludedPatterns = CompileRegex(opts.Logging?.ExcludedPaths ?? []);
        _sensitiveJsonFields = new HashSet<string>(
            HttpBodyLoggingOptions.DefaultSensitiveJsonFields.Concat(_bodyOptions.AdditionalSensitiveJsonFields ?? []),
            StringComparer.OrdinalIgnoreCase);
        _sensitiveHeaderNames = new HashSet<string>(
            HttpBodyLoggingOptions.DefaultSensitiveHeaderNames.Concat(_bodyOptions.AdditionalSensitiveHeaderNames ?? []),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task Invoke(HttpContext context)
    {
        if (!ShouldProcess(context))
        {
            await _next(context);
            return;
        }

        string? requestBody = null;
        int? requestBodySize = null;
        bool requestTruncated = false;
        bool requestCaptured = false;

        if (_bodyOptions.EnableRequestBody && CanReadRequestBody(context.Request))
        {
            (requestBody, requestBodySize, requestTruncated, requestCaptured) =
                await CaptureRequestBodyAsync(context.Request);
        }

        var requestHeadersJson = HeadersToJson(context.Request.Headers, _sensitiveHeaderNames);

        var originalResponseBody = context.Response.Body;
        await using var responseBuffer = new MemoryStream();
        context.Response.Body = responseBuffer;

        string? responseBody = null;
        int? responseBodySize = null;
        bool responseTruncated = false;
        bool responseCaptured = false;

        try
        {
            await _next(context);

            if (_bodyOptions.EnableResponseBody && CanReadResponseBody(context.Response))
            {
                (responseBody, responseBodySize, responseTruncated, responseCaptured) =
                    await CaptureResponseBodyAsync(context.Response, responseBuffer);
            }

            var responseHeadersJson = HeadersToJson(context.Response.Headers, _sensitiveHeaderNames);

            LogBodies(
                context,
                requestBody,
                responseBody,
                requestBodySize,
                responseBodySize,
                requestTruncated,
                responseTruncated,
                requestCaptured,
                responseCaptured,
                requestHeadersJson,
                responseHeadersJson);

            responseBuffer.Position = 0;
            await responseBuffer.CopyToAsync(originalResponseBody, context.RequestAborted);
        }
        catch (Exception ex)
        {
            if (_bodyOptions.EnableResponseBody && CanReadResponseBody(context.Response))
            {
                try
                {
                    (responseBody, responseBodySize, responseTruncated, responseCaptured) =
                        await CaptureResponseBodyAsync(context.Response, responseBuffer);
                }
                catch
                {
                    // Swallow secondary capture errors
                }
            }

            var responseHeadersJson = HeadersToJson(context.Response.Headers, _sensitiveHeaderNames);
            var path = context.Request.Path.Value ?? "";
            var statusCode = context.Response.StatusCode;
            var errorScope = new List<KeyValuePair<string, object?>>
            {
                new("Method", context.Request.Method),
                new("RequestPath", path),
                new("StatusCode", statusCode),
                new("TraceId", Activity.Current?.TraceId.ToString()),
                new("SpanId", Activity.Current?.SpanId.ToString()),
                new("RequestHeaders", requestHeadersJson),
                new("ResponseHeaders", responseHeadersJson),
                new("RequestBody", requestBody),
                new("ResponseBody", responseBody)
            };
            AddEnricherProperties(errorScope, context, requestHeadersJson, responseHeadersJson);
            using (_logger.BeginScope(errorScope))
            {
                _logger.LogError(ex,
                    "HTTP request/response failed. Path: {Path}, StatusCode: {StatusCode}",
                    path,
                    statusCode);
            }

            responseBuffer.Position = 0;
            await responseBuffer.CopyToAsync(originalResponseBody, context.RequestAborted);

            throw;
        }
        finally
        {
            context.Response.Body = originalResponseBody;
        }
    }

    /// <summary>
    /// True if this request should be logged (path not in ExcludedPaths). Request/response body inclusion is controlled by EnableRequestBody/EnableResponseBody when writing the log.
    /// </summary>
    private bool ShouldProcess(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (string.IsNullOrEmpty(path))
            return true;

        return !_excludedPatterns.Any(pattern =>
        {
            try
            {
                return pattern.IsMatch(path);
            }
            catch
            {
                return false;
            }
        });
    }

    private bool CanReadRequestBody(HttpRequest request)
    {
        if (request.Body is null || !request.Body.CanRead)
            return false;

        if (string.IsNullOrWhiteSpace(request.ContentType))
            return false;

        return IsAllowedContentType(request.ContentType);
    }

    private bool CanReadResponseBody(HttpResponse response)
    {
        if (string.IsNullOrWhiteSpace(response.ContentType))
            return false;

        return IsAllowedContentType(response.ContentType);
    }

    private bool IsAllowedContentType(string contentType)
    {
        var normalized = contentType.Split(';', 2)[0].Trim();
        var allowed = new HashSet<string>(_bodyOptions.AllowedContentTypes ?? [], StringComparer.OrdinalIgnoreCase);

        if (allowed.Contains(normalized))
            return true;

        if (normalized.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
            return true;

        if (normalized.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private async Task<(string? Body, int? Size, bool Truncated, bool Captured)> CaptureRequestBodyAsync(HttpRequest request)
    {
        request.EnableBuffering();
        request.Body.Position = 0;

        using var reader = new StreamReader(
            request.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);

        var fullText = await reader.ReadToEndAsync();
        request.Body.Position = 0;

        if (string.IsNullOrEmpty(fullText))
            return (null, 0, false, true);

        var size = Encoding.UTF8.GetByteCount(fullText);
        var redacted = RedactIfPossible(fullText, request.ContentType, _sensitiveJsonFields);

        var truncated = false;
        var maxLength = _bodyOptions.MaxBodyLengthToCapture;
        if (Encoding.UTF8.GetByteCount(redacted) > maxLength)
        {
            redacted = TruncateUtf8(redacted, maxLength);
            truncated = true;
        }

        return (redacted, size, truncated, true);
    }

    private async Task<(string? Body, int? Size, bool Truncated, bool Captured)> CaptureResponseBodyAsync(
        HttpResponse response,
        MemoryStream buffer)
    {
        buffer.Position = 0;

        using var reader = new StreamReader(
            buffer,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);

        var fullText = await reader.ReadToEndAsync();
        buffer.Position = 0;

        if (string.IsNullOrEmpty(fullText))
            return (null, 0, false, true);

        var size = Encoding.UTF8.GetByteCount(fullText);
        var redacted = RedactIfPossible(fullText, response.ContentType, _sensitiveJsonFields);

        var truncated = false;
        var maxLength = _bodyOptions.MaxBodyLengthToCapture;
        if (Encoding.UTF8.GetByteCount(redacted) > maxLength)
        {
            redacted = TruncateUtf8(redacted, maxLength);
            truncated = true;
        }

        return (redacted, size, truncated, true);
    }

    private void LogBodies(
        HttpContext context,
        string? requestBody,
        string? responseBody,
        int? requestBodySize,
        int? responseBodySize,
        bool requestTruncated,
        bool responseTruncated,
        bool requestCaptured,
        bool responseCaptured,
        string? requestHeadersJson,
        string? responseHeadersJson)
    {
        var path = context.Request.Path.Value ?? "";
        var statusCode = context.Response.StatusCode;

        var scope = new List<KeyValuePair<string, object?>>
        {
            new("Method", context.Request.Method),
            new("RequestPath", path),
            new("StatusCode", statusCode),
            new("TraceId", Activity.Current?.TraceId.ToString()),
            new("SpanId", Activity.Current?.SpanId.ToString()),
            new("RequestHeaders", requestHeadersJson),
            new("ResponseHeaders", responseHeadersJson),
            new("RequestBodyCaptured", requestCaptured),
            new("RequestBodySize", requestBodySize),
            new("RequestBodyTruncated", requestTruncated),
            new("RequestBody", requestBody),
            new("ResponseBodyCaptured", responseCaptured),
            new("ResponseBodySize", responseBodySize),
            new("ResponseBodyTruncated", responseTruncated),
            new("ResponseBody", responseBody)
        };

        AddEnricherProperties(scope, context, requestHeadersJson, responseHeadersJson);

        using (_logger.BeginScope(scope))
        {
            _logger.LogInformation(
                "HTTP request/response logged. Path: {Path}, StatusCode: {StatusCode}",
                path,
                statusCode);
        }
    }

    private void AddEnricherProperties(
        List<KeyValuePair<string, object?>> scope,
        HttpContext context,
        string? requestHeadersJson,
        string? responseHeadersJson)
    {
        if (_enricherOptions.CustomAttributes != null)
        {
            foreach (var kv in _enricherOptions.CustomAttributes)
            {
                scope.Add(new KeyValuePair<string, object?>(kv.Key, kv.Value));
            }
        }

        var headerNames = _enricherOptions.Headers;
        if (headerNames == null || headerNames.Count == 0)
            return;

        foreach (var headerName in headerNames)
        {
            if (string.IsNullOrWhiteSpace(headerName))
                continue;
            var key = headerName.Trim();
            var requestKey = $"RequestHeader.{key.Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant()}";
            var responseKey = $"ResponseHeader.{key.Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant()}";
            var isSensitive = _sensitiveHeaderNames.Contains(key);

            if (context.Request.Headers.TryGetValue(key, out var reqVal))
                scope.Add(new KeyValuePair<string, object?>(requestKey, isSensitive ? "***REDACTED***" : reqVal.ToString()));
            if (context.Response.Headers.TryGetValue(key, out var resVal))
                scope.Add(new KeyValuePair<string, object?>(responseKey, isSensitive ? "***REDACTED***" : resVal.ToString()));
        }
    }

    private static string? HeadersToJson(IHeaderDictionary headers, HashSet<string> sensitiveHeaderNames)
    {
        if (headers == null || headers.Count == 0)
            return null;
        try
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in headers)
            {
                var normalizedKey = key.ToLowerInvariant();
                var valueStr = value.ToString();
                dict[normalizedKey] = sensitiveHeaderNames.Contains(key)
                    ? "***REDACTED***"
                    : valueStr;
            }
            return JsonSerializer.Serialize(dict);
        }
        catch
        {
            return null;
        }
    }

    private static List<Regex> CompileRegex(IEnumerable<string> patterns)
        => patterns.Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase)).ToList();

    private static string TruncateUtf8(string value, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maxBytes)
            return value;

        var truncated = Encoding.UTF8.GetString(bytes, 0, maxBytes);
        return truncated + "…[TRUNCATED]";
    }

    private static string RedactIfPossible(string text, string? contentType, HashSet<string> sensitiveJsonFields)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        if (contentType is null)
            return text;

        var normalized = contentType.Split(';', 2)[0].Trim();

        if (normalized.Contains("json", StringComparison.OrdinalIgnoreCase) || normalized.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
            return RedactJson(text, sensitiveJsonFields);

        return text;
    }

    private static string RedactJson(string json, HashSet<string> sensitiveJsonFields)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var redacted = RedactElement(doc.RootElement, sensitiveJsonFields);
            return JsonSerializer.Serialize(redacted);
        }
        catch
        {
            return json;
        }
    }

    private static object? RedactElement(JsonElement element, HashSet<string> sensitiveJsonFields)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(
                    p => p.Name,
                    p => sensitiveJsonFields.Contains(p.Name)
                        ? "***REDACTED***"
                        : RedactElement(p.Value, sensitiveJsonFields)),
            JsonValueKind.Array => element.EnumerateArray().Select(x => RedactElement(x, sensitiveJsonFields)).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l :
                element.TryGetDecimal(out var d) ? d :
                element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.ToString()
        };
    }
}
