using System;
using System.Collections.Generic;
using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

namespace BBT.Aether.AspNetCore.Telemetry;

/// <summary>
/// The OpenTelemetry counterpart of <see cref="FilteredSpanParentPropagator"/>: stops a filtered span
/// from becoming the parent a remote service sees.
/// </summary>
/// <remarks>
/// <para>
/// Both wrappers are needed, and which one lands on the wire is not obvious. .NET's
/// <c>DiagnosticsHandler</c> injects through <see cref="DistributedContextPropagator"/> and *then*
/// raises the DiagnosticSource start event; OpenTelemetry's HttpClient instrumentation handles that
/// event and injects again, with the just-created <c>System.Net.Http.HttpRequestOut</c> context —
/// overwriting whatever was already there. Measured on 2026-09-13: with only the
/// <see cref="DistributedContextPropagator"/> wrapper installed, the header written was verifiably
/// the recorded gRPC span's id, and the Dapr sidecar still reported a parent that matched no
/// exported span. The ids it used were the filtered activity's, i.e. the second injection's.
/// </para>
/// <para>
/// So the rule has to be enforced at both layers. This one applies it where OpenTelemetry injects:
/// when the context handed to it is not <see cref="ActivityTraceFlags.Recorded"/>, walk
/// <see cref="Activity.Current"/> up to the nearest ancestor that is, and propagate that instead.
/// </para>
/// <para>
/// Baggage travels unchanged. Only the parent id and its trace flags are redirected — the trace id
/// is the same on every ancestor, so a redirect can never move the call into a different trace.
/// </para>
/// </remarks>
/// <param name="inner">The propagator that does the actual header work, normally the W3C one.</param>
public sealed class FilteredSpanParentTextMapPropagator(TextMapPropagator inner) : TextMapPropagator
{
    private readonly TextMapPropagator _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public override ISet<string> Fields => _inner.Fields!;

    /// <inheritdoc />
    public override PropagationContext Extract<T>(
        PropagationContext context,
        T carrier,
        Func<T, string, IEnumerable<string>?> getter) =>
        _inner.Extract(context, carrier, getter);

    /// <inheritdoc />
    public override void Inject<T>(
        PropagationContext context,
        T carrier,
        Action<T, string, string> setter)
    {
        var target = FilteredSpanRedirect.Target(Activity.Current, carrier);

        // Only redirect when the current activity really is the one being injected; otherwise the
        // context came from somewhere we cannot reason about and must travel untouched.
        var redirected = target is not null
                         && Activity.Current is not null
                         && Activity.Current.Context.SpanId == context.ActivityContext.SpanId
                         && target != Activity.Current
            ? new PropagationContext(target.Context, context.Baggage)
            : context;

        _inner.Inject(redirected, carrier, setter);
    }

}
