using System.Diagnostics;
using System.Net.Http;

namespace BBT.Aether.AspNetCore.Telemetry;

/// <summary>
/// The one decision both propagator wrappers make: when the request about to go out is a Dapr
/// diagnostic call — a span the tracing filter will drop — the parent written to the wire must be
/// the enclosing activity, not the one being dropped.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect.</b> Filtering a span does not remove its <see cref="Activity"/>. The activity is
/// created, its id is written into the outgoing <c>traceparent</c>, and only afterwards is it marked
/// unrecorded. The receiver cannot know the id names a span nobody will write: a Dapr sidecar with
/// <c>samplingRate: "1"</c> samples on its own terms and records a span whose parent document never
/// arrives. Elastic APM resolves nesting strictly through <c>parent.id</c> and re-roots such a span
/// to the trace root; OpenObserve groups by trace id and hides it, which is why the same deployment
/// looks healthy on one backend and littered on the other.
/// </para>
/// <para>
/// <b>Why the obvious test does not work.</b> The natural rule — "if this context is not recorded,
/// propagate its nearest recorded ancestor" — is unusable, because the flag has not been cleared
/// yet. Measured on 2026-09-13 by logging every injection: the context handed to the propagator for
/// a filtered Dapr state call arrives as <c>Recorded</c>, with
/// <c>Activity.Current = System.Net.Http.HttpRequestOut</c>. OpenTelemetry's HttpClient
/// instrumentation injects the headers first and applies <c>FilterHttpRequestMessage</c> afterwards.
/// At injection time nothing about the activity distinguishes it from one that will be exported.
/// </para>
/// <para>
/// <b>What does work.</b> The carrier is the <see cref="HttpRequestMessage"/> itself, so the same
/// predicate the filter will apply can be applied here, before the header is written — and
/// <see cref="DaprDiagnosticRequest"/> is that predicate, shared rather than copied precisely so the
/// two decisions cannot drift apart. The parent then becomes the enclosing activity, which for a
/// Dapr state or lock call is the <c>dapr.proto.runtime.v1.Dapr/GetState</c> gRPC client span the
/// framework already exports.
/// </para>
/// </remarks>
internal static class FilteredSpanRedirect
{
    /// <summary>
    /// The activity whose id should go on the wire for <paramref name="carrier"/>, or
    /// <paramref name="current"/> when nothing needs redirecting.
    /// </summary>
    /// <remarks>
    /// Both guards are load-bearing. A carrier that is not an <see cref="HttpRequestMessage"/> is
    /// some other transport we know nothing about; and a redirect is only safe while
    /// <paramref name="current"/> really is the activity for this request, which is what makes its
    /// <see cref="Activity.Parent"/> the enclosing span rather than an unrelated one. A parentless
    /// activity is left alone: propagating nothing would start a fresh trace at the receiver and
    /// break the correlation this exists to protect.
    /// </remarks>
    internal static Activity? Target(Activity? current, object? carrier) =>
        carrier is HttpRequestMessage request
        && DaprDiagnosticRequest.Matches(request.RequestUri)
        && current is { Parent: not null }
            ? current.Parent
            : current;
}
