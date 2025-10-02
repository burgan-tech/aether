using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace BBT.Aether.AspNetCore.Telemetry.Enrichers;

public class HeaderLogEnricher(
    IConfiguration configuration,
    IHttpContextAccessor httpContextAccessor)
    : ILogEventEnricher
{
    private readonly IEnumerable<string> _headerNames = configuration.GetSection("Telemetry:Logging:Enrichers:Headers").Get<string[]>() ?? [];

    public virtual void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext == null) return;

        foreach (var headerName in _headerNames)
        {
            var headerValue = httpContext.Request.Headers[headerName].ToString();
            if (!string.IsNullOrEmpty(headerValue))
            {
                logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(headerName, headerValue));
            }
        }
    }
}