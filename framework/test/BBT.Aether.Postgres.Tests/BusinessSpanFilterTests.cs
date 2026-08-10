using System.Collections.Generic;
using System.Diagnostics;
using BBT.Aether.AspNetCore.Telemetry;
using BBT.Aether.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Xunit;

namespace BBT.Aether.Postgres.Tests;

public sealed class BusinessSpanFilterTests
{
    private const string ActivitySourceName = "BBT.Aether.Tests.BusinessSpanFilter";

    [Fact]
    public void Tracing_options_default_to_business()
    {
        Assert.Equal(AetherTracingDetailLevel.Business, new AetherTracingOptions().DetailLevel);
    }

    [Fact]
    public void Business_profile_filters_only_bracket_prefixed_display_names()
    {
        var exportedNames = ExportTwoActivities(AetherTracingDetailLevel.Business);

        Assert.Equal(["transition/start"], exportedNames);
    }

    [Fact]
    public void Verbose_profile_keeps_bracket_prefixed_display_names()
    {
        var exportedNames = ExportTwoActivities(AetherTracingDetailLevel.Verbose);

        Assert.Equal(["[20] CreateTransitionRecordStep", "transition/start"], exportedNames);
    }

    private static List<string> ExportTwoActivities(AetherTracingDetailLevel detailLevel)
    {
        var exportedNames = new List<string>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telemetry:ServiceName"] = "business-span-filter-tests",
                ["Telemetry:MetricsEnabled"] = "false",
                ["Telemetry:LoggingEnabled"] = "false",
                ["Telemetry:Tracing:DetailLevel"] = detailLevel.ToString(),
                ["Telemetry:Tracing:EnableAspNetCore"] = "false",
                ["Telemetry:Tracing:EnableHttpClient"] = "false",
                ["Telemetry:Tracing:EnableEntityFrameworkCore"] = "false",
                ["Telemetry:Tracing:EnableConsoleExporter"] = "false",
                ["Telemetry:Tracing:EnableOtlpExporter"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddAetherTelemetry(
            configuration,
            configure: builder => builder.ConfigureTracing((_, tracing) =>
            {
                tracing.AddSource(ActivitySourceName);
                tracing.AddProcessor(
                    new SimpleActivityExportProcessor(new CapturingActivityExporter(exportedNames)));
            }));

        using var serviceProvider = services.BuildServiceProvider();
        using var tracerProvider = serviceProvider.GetRequiredService<TracerProvider>();
        using var source = new ActivitySource(ActivitySourceName);

        using (var pipelineStep = source.StartActivity("PipelineStep.ExecuteAsync"))
        {
            Assert.NotNull(pipelineStep);
            pipelineStep.DisplayName = "[20] CreateTransitionRecordStep";
        }

        using (var transition = source.StartActivity("TransitionExecutor.ExecuteOneAsync"))
        {
            Assert.NotNull(transition);
            transition.DisplayName = "transition/start";
        }

        return exportedNames;
    }

    private sealed class CapturingActivityExporter(List<string> exportedNames) : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
            {
                exportedNames.Add(activity.DisplayName);
            }

            return ExportResult.Success;
        }
    }
}
