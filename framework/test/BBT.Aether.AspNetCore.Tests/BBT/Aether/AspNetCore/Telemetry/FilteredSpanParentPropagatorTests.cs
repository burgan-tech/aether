using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using BBT.Aether.AspNetCore.Telemetry;
using Shouldly;
using Xunit;

namespace BBT.Aether.AspNetCore.Tests.BBT.Aether.AspNetCore.Telemetry;

/// <summary>
/// The contract is one sentence: a span the tracing filter is about to drop must never be the
/// parent a remote service sees.
/// <para>
/// The tests drive the public propagator with a real <see cref="HttpRequestMessage"/> rather than
/// calling the internal decision directly, because the request URI is the whole input — a test that
/// bypassed the carrier would pass while the propagator read the wrong thing off it.
/// </para>
/// </summary>
public class FilteredSpanParentPropagatorTests
{
    // A const, never ActivitySource.Name: AddActivityListener invokes every registered predicate
    // while it constructs a source, so a predicate that reads the field re-enters the static
    // initializer that is still running and poisons the type for the rest of the process.
    private const string SourceName = "FilteredSpanParentPropagatorTests";

    private static readonly ActivitySource Source = new(SourceName);

    private const string DaprStateCall = "http://localhost:42111/dapr.proto.runtime.v1.Dapr/GetState";
    private const string DaprLockCall = "http://localhost:42111/dapr.proto.runtime.v1.Dapr/TryLockAlpha1";
    private const string OrdinaryCall = "http://partner-domain/api/v1/partner/workflows/x/instances/y";

    [Theory]
    [InlineData(DaprStateCall)]
    [InlineData(DaprLockCall)]
    public void Inject_ForADaprDiagnosticCall_PropagatesTheEnclosingSpan(string url)
    {
        using var listener = Listen();

        using var enclosing = Source.StartActivity("dapr.proto.runtime.v1.Dapr/GetState")!;
        using var transport = Source.StartActivity("System.Net.Http.HttpRequestOut")!;

        ParentIdWrittenFor(transport, url).ShouldBe(enclosing.SpanId.ToHexString(),
            "the wire must name the gRPC client span, which is exported, and not the transport span, " +
            "which the filter is about to drop");
    }

    [Fact]
    public void Inject_ForAnOrdinaryRequest_PropagatesTheRequestsOwnSpan()
    {
        using var listener = Listen();

        using var enclosing = Source.StartActivity("caller")!;
        using var transport = Source.StartActivity("System.Net.Http.HttpRequestOut")!;

        ParentIdWrittenFor(transport, OrdinaryCall).ShouldBe(transport.SpanId.ToHexString(),
            "nothing about this request is filtered, so the propagator must not alter the trace");
    }

    [Fact]
    public void Inject_WhenTheRequestSpanHasNoParent_ChangesNothing()
    {
        using var listener = Listen();

        using var orphanRoot = Source.StartActivity("System.Net.Http.HttpRequestOut")!;

        // Propagating nothing would start a fresh trace at the receiver and break the correlation
        // this propagator exists to protect, so a parentless span travels as itself.
        ParentIdWrittenFor(orphanRoot, DaprStateCall).ShouldBe(orphanRoot.SpanId.ToHexString());
    }

    /// <summary>Runs Inject through the real W3C propagator and returns the parent-id it wrote.</summary>
    private static string? ParentIdWrittenFor(Activity activity, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        var propagator = new FilteredSpanParentPropagator(DistributedContextPropagator.CreateDefaultPropagator());
        var headers = new Dictionary<string, string>();

        propagator.Inject(activity, request, (_, key, value) => headers[key] = value);

        // traceparent is `00-<trace>-<parent-span>-<flags>`; the third field is the claim under test.
        return headers.TryGetValue("traceparent", out var traceparent)
            ? traceparent.Split('-')[2]
            : null;
    }

    /// <summary>
    /// Without a listener that samples, <c>StartActivity</c> returns null and every test here would
    /// silently pass on a null-forgiving dereference.
    /// </summary>
    private static ActivityListener Listen()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
