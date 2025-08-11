using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.AspNetCore.Telemetry;

public class TelemetryOptions
{
    public string? ServiceName { get; set; } = "";
    public string? ServiceVersion { get; set; } = "";
    public string Environment { get; set; } = "Development";
    public string TraceProvider { get; set; } = "zipkin";
    public string LogProvider { get; set; } = "console";
    public LoggingOptions Logging { get; set; } = new();
    public ZipkinOptions Zipkin { get; set; } = new();
    public ElasticOptions Elastic { get; set; } = new();
    public OtlpOptions Otlp { get; set; } = new();
    public OpenObserveOptions OpenObserve { get; set; } = new();
}

public class LoggingOptions
{
    public bool Enabled { get; set; } = true;
    public string FilePath { get; set; } = "logs/app.log";
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
    public List<string> ExcludedPaths { get; set; } = new();
    public LoggingEnrichers Enrichers { get; set; } = new();
}

public class LoggingEnrichers
{
    public List<string> Headers { get; set; } = new(); 
}

public class ZipkinOptions
{
    public string Endpoint { get; set; } = "http://localhost:9411/api/v2/spans";
}

public class ElasticOptions
{
    public string Endpoint { get; set; } = "http://localhost:8200";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public class OtlpOptions
{
    public string Endpoint { get; set; } = "http://localhost:4317";
}

public class OpenObserveOptions
{
    public string Endpoint { get; set; } = "http://localhost:5080/api/default";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Organization { get; set; } = "default";
    public string Stream { get; set; } = "default";
}