using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using BBT.Aether.Aspects;
using BBT.Aether.Telemetry;
using Xunit;

namespace BBT.Aether.Tracing;

public sealed class TraceDetailLevelTests
{
    [Fact]
    public void Runtime_uses_global_detail_level()
    {
        Assert.Equal(AetherTracingDetailLevel.Business, AetherTracingRuntime.DetailLevel);

        try
        {
            AetherTracingRuntime.Configure(AetherTracingDetailLevel.Business);

            Assert.False(AetherTracingRuntime.IsVerbose);

            AetherTracingRuntime.Configure(AetherTracingDetailLevel.Verbose);

            Assert.True(AetherTracingRuntime.IsVerbose);
        }
        finally
        {
            AetherTracingRuntime.Configure(AetherTracingDetailLevel.Business);
        }
    }

    [Fact]
    public async Task Trace_annotation_creates_business_span_in_business_profile()
    {
        var startedActivities = new List<Activity>();
        using var listener = CreateListener(startedActivities);

        try
        {
            AetherTracingRuntime.Configure(AetherTracingDetailLevel.Business);

            var probe = new TraceProbe();
            await probe.ExecuteAsync();

            Assert.True(probe.Executed);
            var activity = Assert.Single(startedActivities);
            Assert.Equal("TraceProbe.ExecuteAsync", activity.OperationName);
        }
        finally
        {
            AetherTracingRuntime.Configure(AetherTracingDetailLevel.Business);
        }
    }

    [Fact]
    public async Task Trace_annotation_creates_span_in_verbose_profile()
    {
        var startedActivities = new List<Activity>();
        using var listener = CreateListener(startedActivities);

        try
        {
            AetherTracingRuntime.Configure(AetherTracingDetailLevel.Verbose);

            await new TraceProbe().ExecuteAsync();

            var activity = Assert.Single(startedActivities);
            Assert.Equal("TraceProbe.ExecuteAsync", activity.OperationName);
        }
        finally
        {
            AetherTracingRuntime.Configure(AetherTracingDetailLevel.Business);
        }
    }

    [Fact]
    public void Infrastructure_diagnostics_follow_global_detail_level()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == InfrastructureActivitySource.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        try
        {
            AetherTracingRuntime.Configure(AetherTracingDetailLevel.Business);
            using var businessActivity = InfrastructureActivitySource.StartDiagnosticActivity(
                "DistributedCache.Get",
                ActivityKind.Client);

            Assert.Null(businessActivity);

            AetherTracingRuntime.Configure(AetherTracingDetailLevel.Verbose);
            using var verboseActivity = InfrastructureActivitySource.StartDiagnosticActivity(
                "DistributedCache.Get",
                ActivityKind.Client);

            Assert.NotNull(verboseActivity);
            Assert.Equal("DistributedCache.Get", verboseActivity.OperationName);
        }
        finally
        {
            AetherTracingRuntime.Configure(AetherTracingDetailLevel.Business);
        }
    }

    private static ActivityListener CreateListener(List<Activity> startedActivities)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AetherActivitySource.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                if (activity.OperationName == "TraceProbe.ExecuteAsync")
                {
                    startedActivities.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class TraceProbe
    {
        public bool Executed { get; private set; }

        [Trace]
        public Task ExecuteAsync()
        {
            Executed = true;
            return Task.CompletedTask;
        }
    }
}
