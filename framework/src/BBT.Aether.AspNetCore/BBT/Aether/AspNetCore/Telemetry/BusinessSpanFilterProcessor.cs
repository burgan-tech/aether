using System;
using System.Diagnostics;
using OpenTelemetry;

namespace BBT.Aether.AspNetCore.Telemetry;

/// <summary>
/// Removes pipeline-detail spans from the export path when the Business tracing profile is active.
/// </summary>
/// <remarks>
/// Pipeline steps assign their final display names while the activity is running, so this filter
/// must run on completion rather than at sampling time. Clearing the recorded flag allows the
/// standard OpenTelemetry activity export processors to skip the span while preserving its
/// in-process activity context for any child operations created during execution.
/// </remarks>
internal sealed class BusinessSpanFilterProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        if (activity.DisplayName.StartsWith("[", StringComparison.Ordinal))
        {
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        }
    }
}
