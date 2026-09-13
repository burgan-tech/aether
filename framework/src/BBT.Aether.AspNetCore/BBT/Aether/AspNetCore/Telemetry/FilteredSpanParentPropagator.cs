using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace BBT.Aether.AspNetCore.Telemetry;

/// <summary>
/// Wraps the ambient <see cref="DistributedContextPropagator"/> so a span the tracing filter is
/// about to drop never becomes the parent a remote service sees.
/// </summary>
/// <remarks>
/// <para>
/// The decision itself lives in <see cref="FilteredSpanRedirect"/>, which also carries the
/// measurements: why the obvious "is it recorded?" test cannot work, and which three narrower fixes
/// were tried and refuted first.
/// </para>
/// <para>
/// This layer is .NET's own injection, which <c>DiagnosticsHandler</c> performs before it raises the
/// DiagnosticSource start event. In a host where OpenTelemetry's HttpClient instrumentation is also
/// active, the second injection overwrites this one — so
/// <see cref="FilteredSpanParentTextMapPropagator"/> exists as well and the two must agree. This one
/// is what protects a host that has HttpClient instrumentation turned off.
/// </para>
/// </remarks>
/// <param name="inner">The propagator to delegate the actual header work to.</param>
public sealed class FilteredSpanParentPropagator(DistributedContextPropagator inner) : DistributedContextPropagator
{
    private readonly DistributedContextPropagator _inner =
        inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Fields => _inner.Fields;

    /// <inheritdoc />
    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter) =>
        _inner.Inject(FilteredSpanRedirect.Target(activity, carrier), carrier, setter);

    /// <inheritdoc />
    public override void ExtractTraceIdAndState(
        object? carrier,
        PropagatorGetterCallback? getter,
        out string? traceId,
        out string? traceState) =>
        _inner.ExtractTraceIdAndState(carrier, getter, out traceId, out traceState);

    /// <inheritdoc />
    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(
        object? carrier,
        PropagatorGetterCallback? getter) =>
        _inner.ExtractBaggage(carrier, getter);

}
