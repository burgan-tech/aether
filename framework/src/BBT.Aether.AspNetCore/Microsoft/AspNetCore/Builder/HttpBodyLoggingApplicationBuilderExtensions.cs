using BBT.Aether.AspNetCore.Telemetry;
using Microsoft.AspNetCore.Builder;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Extension methods for HTTP request/response body logging middleware.
/// </summary>
public static class HttpBodyLoggingApplicationBuilderExtensions
{
    /// <summary>
    /// Adds middleware that captures and logs HTTP request/response bodies (logging only; no trace tags).
    /// Configure via <c>Telemetry:Logging:Body</c> and path exclusion via <c>Telemetry:Logging:ExcludedPaths</c>.
    /// </summary>
    /// <remarks>
    /// Place after UseAuthentication/UseAuthorization and before MapControllers so the response body is captured after the pipeline runs.
    /// </remarks>
    public static IApplicationBuilder UseHttpBodyLogging(this IApplicationBuilder app)
    {
        return app.UseMiddleware<HttpBodyLoggingMiddleware>();
    }
}
