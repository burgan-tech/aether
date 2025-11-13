using System;
using OpenTelemetry.Exporter;

namespace BBT.Aether.AspNetCore.Telemetry;

public static class OtlpExporterConfigurator
{
    public static void Configure(OtlpExporterOptions options, OtlpOptions otlp, string signalPath)
    {
        var protocol = string.Equals(otlp.Protocol, "grpc", StringComparison.OrdinalIgnoreCase)
            ? OtlpExportProtocol.Grpc
            : OtlpExportProtocol.HttpProtobuf;

        var endpointBase = otlp.Endpoint?.TrimEnd('/') ?? "http://localhost:4318";

        var endpoint = protocol == OtlpExportProtocol.Grpc
            ? endpointBase
            : $"{endpointBase}{signalPath}";

        options.Protocol = protocol;
        options.Endpoint = new Uri(endpoint);
    }
}

