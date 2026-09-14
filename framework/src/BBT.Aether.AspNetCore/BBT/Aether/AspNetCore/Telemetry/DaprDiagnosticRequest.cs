using System;

namespace BBT.Aether.AspNetCore.Telemetry;

/// <summary>
/// The single definition of "a Dapr call that is diagnostic noise rather than business work" —
/// state store, lock, secret and configuration operations, in both their gRPC and HTTP API spellings.
/// </summary>
/// <remarks>
/// <para>
/// It lives in its own type because two very different places must agree on it, and a copy in either
/// would be a silent trap. The tracing filter uses it to decide that a span is not worth exporting;
/// <see cref="FilteredSpanParentTextMapPropagator"/> uses it to decide that the same span must not
/// become the parent a remote service sees. If those two lists ever disagreed, a request would be
/// filtered but still propagated — which is exactly the orphan this pairing exists to prevent.
/// </para>
/// </remarks>
internal static class DaprDiagnosticRequest
{
    internal static bool Matches(Uri? uri)
    {
        var path = uri?.AbsolutePath;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        if (path.StartsWith("/dapr.proto.runtime.v1.Dapr/", StringComparison.OrdinalIgnoreCase))
        {
            return path.EndsWith("/GetState", StringComparison.OrdinalIgnoreCase)
                   || path.EndsWith("/GetBulkState", StringComparison.OrdinalIgnoreCase)
                   || path.EndsWith("/SaveState", StringComparison.OrdinalIgnoreCase)
                   || path.EndsWith("/DeleteState", StringComparison.OrdinalIgnoreCase)
                   || path.EndsWith("/ExecuteStateTransaction", StringComparison.OrdinalIgnoreCase)
                   || path.EndsWith("/GetSecret", StringComparison.OrdinalIgnoreCase)
                   || path.EndsWith("/GetBulkSecret", StringComparison.OrdinalIgnoreCase)
                   || path.EndsWith("/GetConfiguration", StringComparison.OrdinalIgnoreCase)
                   || path.EndsWith("/SubscribeConfiguration", StringComparison.OrdinalIgnoreCase)
                   || path.EndsWith("/TryLockAlpha1", StringComparison.OrdinalIgnoreCase)
                   || path.EndsWith("/UnlockAlpha1", StringComparison.OrdinalIgnoreCase);
        }

        return path.StartsWith("/v1.0/state/", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("/v1.0-alpha1/state/", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("/v1.0-alpha1/lock/", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("/v1.0/secrets/", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("/v1.0/configuration/", StringComparison.OrdinalIgnoreCase);
    }
}
