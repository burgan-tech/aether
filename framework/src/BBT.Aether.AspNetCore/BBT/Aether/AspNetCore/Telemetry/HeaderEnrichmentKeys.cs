using System;

namespace BBT.Aether.AspNetCore.Telemetry;

/// <summary>
/// Builds the attribute keys used to enrich log records with individual HTTP header values.
/// Shared by <see cref="EnricherLogProcessor"/> (all log records) and
/// <see cref="HttpBodyLoggingMiddleware"/> (the HTTP body log scope) so the two paths cannot
/// drift apart and both honour the configured prefixes.
/// </summary>
internal static class HeaderEnrichmentKeys
{
    /// <summary>
    /// Normalizes a header name for use in an attribute key: lowercase, with '-' replaced by '_'
    /// (e.g. <c>X-Request-Id</c> becomes <c>x_request_id</c>).
    /// </summary>
    internal static string Normalize(string headerName)
        => headerName.Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();

    /// <summary>
    /// Builds the enrich key for a header read from the request, honouring
    /// <see cref="LoggingEnricherOptions.RequestHeaderKeyPrefix"/>.
    /// </summary>
    internal static string Request(LoggingEnricherOptions options, string headerName)
        => $"{options.RequestHeaderKeyPrefix ?? string.Empty}{Normalize(headerName)}";

    /// <summary>
    /// Builds the enrich key for a header read from the response, honouring
    /// <see cref="LoggingEnricherOptions.ResponseHeaderKeyPrefix"/>.
    /// </summary>
    internal static string Response(LoggingEnricherOptions options, string headerName)
        => $"{options.ResponseHeaderKeyPrefix ?? string.Empty}{Normalize(headerName)}";
}
