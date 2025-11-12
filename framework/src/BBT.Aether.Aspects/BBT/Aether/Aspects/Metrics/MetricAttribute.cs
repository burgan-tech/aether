using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Tasks;
using PostSharp.Aspects;
using PostSharp.Extensibility;
using PostSharp.Serialization;

namespace BBT.Aether.Aspects;

/// <summary>
/// Aspect that integrates with OpenTelemetry Metrics and Prometheus to track method execution metrics.
/// Supports Histogram (duration), Counter (invocations), and UpDownCounter (in-progress) metric types.
/// Requires "BBT.Aether.Aspects" Meter to be registered in OpenTelemetry configuration.
/// </summary>
[PSerializable]
[MulticastAttributeUsage(
    MulticastTargets.Method,
    AllowMultiple = false,
    Inheritance = MulticastInheritance.Strict)]
public class MetricAttribute : AetherMethodInterceptionAspect
{
    /// <summary>
    /// Gets or sets the metric type.
    /// Default is Histogram (records duration distribution).
    /// </summary>
    public MetricType Type { get; set; } = MetricType.Histogram;

    /// <summary>
    /// Gets or sets an optional metric name override.
    /// If not provided, the metric name will be generated from the method signature.
    /// </summary>
    public string? MetricName { get; set; }

    /// <summary>
    /// Gets or sets the unit of measurement.
    /// Examples: "ms" for milliseconds, "requests", "items", "bytes"
    /// Default for Histogram is "ms" (milliseconds).
    /// </summary>
    public string? Unit { get; set; }

    /// <summary>
    /// Gets or sets custom tags in "key:value" format.
    /// Example: new[] { "service:orders", "priority:high" }
    /// </summary>
    public string[]? Tags { get; set; }

    /// <summary>
    /// Gets or sets whether to record execution time as a separate histogram.
    /// Only applies when Type is not Histogram.
    /// Default is true.
    /// </summary>
    public bool RecordExecutionTime { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to record method invocation count.
    /// Default is true.
    /// </summary>
    public bool RecordInvocationCount { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to record error count when exceptions occur.
    /// Default is true.
    /// </summary>
    public bool RecordErrorCount { get; set; } = true;

    /// <summary>
    /// Intercepts async method execution to record metrics.
    /// </summary>
    public async override Task OnInvokeAsync(MethodInterceptionArgs args)
    {
        switch (Type)
        {
            case MetricType.Histogram:
                await ExecuteWithHistogramAsync(args);
                break;
            case MetricType.Counter:
                await ExecuteWithCounterAsync(args);
                break;
            case MetricType.UpDownCounter:
                await ExecuteWithUpDownCounterAsync(args);
                break;
            default:
                throw new InvalidOperationException($"Unsupported MetricType: {Type}");
        }
    }

    /// <summary>
    /// Intercepts synchronous method execution to record metrics.
    /// Bridges sync methods to async metrics implementation.
    /// </summary>
    public override void OnInvoke(MethodInterceptionArgs args)
    {
        // Bridge synchronous method to async metrics handling
        OnInvokeAsync(args).GetAwaiter().GetResult();
    }

    #region Histogram Mode

    private async Task ExecuteWithHistogramAsync(MethodInterceptionArgs args)
    {
        var metricName = CreateMetricName(args, "duration");
        var unit = Unit ?? "ms";
        var tags = ParseTags(Tags);

        // Get or create histogram instrument
        var histogram = AetherMeter.GetOrCreateHistogram(
            metricName,
            unit,
            $"Duration of {GetMethodDisplayName(args)}");

        // Get or create invocation counter if enabled
        Counter<long>? invocationCounter = null;
        if (RecordInvocationCount)
        {
            var counterName = CreateMetricName(args, "invocations");
            invocationCounter = AetherMeter.GetOrCreateCounter(
                counterName,
                "invocations",
                $"Number of invocations of {GetMethodDisplayName(args)}");
        }

        // Get or create error counter if enabled
        Counter<long>? errorCounter = null;
        if (RecordErrorCount)
        {
            var errorName = CreateMetricName(args, "errors");
            errorCounter = AetherMeter.GetOrCreateCounter(
                errorName,
                "errors",
                $"Number of errors in {GetMethodDisplayName(args)}");
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Increment invocation counter
            invocationCounter?.Add(1, tags);

            // Call extensibility point before execution
            await OnBeforeAsync(args);

            // Execute the method
            await args.ProceedAsync();

            stopwatch.Stop();

            // Record duration
            var duration = unit == "ms" ? stopwatch.ElapsedMilliseconds : stopwatch.Elapsed.TotalSeconds;
            histogram.Record(duration, tags);

            // Call extensibility point after execution
            await OnAfterAsync(args);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            // Record duration even on error
            var duration = unit == "ms" ? stopwatch.ElapsedMilliseconds : stopwatch.Elapsed.TotalSeconds;
            var errorTags = CloneTags(tags);
            errorTags.Add("error", "true");
            errorTags.Add("exception.type", ex.GetType().Name);
            histogram.Record(duration, errorTags);

            // Increment error counter
            errorCounter?.Add(1, errorTags);

            // Call extensibility point on exception
            await OnExceptionAsync(args, ex);

            // Re-throw the exception
            throw;
        }
    }

    #endregion

    #region Counter Mode

    private async Task ExecuteWithCounterAsync(MethodInterceptionArgs args)
    {
        var metricName = CreateMetricName(args, "invocations");
        var unit = Unit ?? "invocations";
        var tags = ParseTags(Tags);

        // Get or create counter instrument
        var counter = AetherMeter.GetOrCreateCounter(
            metricName,
            unit,
            $"Number of invocations of {GetMethodDisplayName(args)}");

        // Get or create error counter if enabled
        Counter<long>? errorCounter = null;
        if (RecordErrorCount)
        {
            var errorName = CreateMetricName(args, "errors");
            errorCounter = AetherMeter.GetOrCreateCounter(
                errorName,
                "errors",
                $"Number of errors in {GetMethodDisplayName(args)}");
        }

        // Get or create duration histogram if enabled
        Histogram<double>? durationHistogram = null;
        Stopwatch? stopwatch = null;
        if (RecordExecutionTime)
        {
            var durationName = CreateMetricName(args, "duration");
            durationHistogram = AetherMeter.GetOrCreateHistogram(
                durationName,
                "ms",
                $"Duration of {GetMethodDisplayName(args)}");
            stopwatch = Stopwatch.StartNew();
        }

        try
        {
            // Increment counter on method entry
            counter.Add(1, tags);

            // Call extensibility point before execution
            await OnBeforeAsync(args);

            // Execute the method
            await args.ProceedAsync();

            // Record duration if enabled
            if (stopwatch != null && durationHistogram != null)
            {
                stopwatch.Stop();
                durationHistogram.Record(stopwatch.ElapsedMilliseconds, tags);
            }

            // Call extensibility point after execution
            await OnAfterAsync(args);
        }
        catch (Exception ex)
        {
            // Record duration even on error if enabled
            if (stopwatch != null && durationHistogram != null)
            {
                stopwatch.Stop();
                var errorTags = CloneTags(tags);
                errorTags.Add("error", "true");
                errorTags.Add("exception.type", ex.GetType().Name);
                durationHistogram.Record(stopwatch.ElapsedMilliseconds, errorTags);
            }

            // Increment error counter
            var exceptionTags = CloneTags(tags);
            exceptionTags.Add("exception.type", ex.GetType().Name);
            errorCounter?.Add(1, exceptionTags);

            // Call extensibility point on exception
            await OnExceptionAsync(args, ex);

            // Re-throw the exception
            throw;
        }
    }

    #endregion

    #region UpDownCounter Mode

    private async Task ExecuteWithUpDownCounterAsync(MethodInterceptionArgs args)
    {
        var metricName = CreateMetricName(args, "in_progress");
        var unit = Unit ?? "operations";
        var tags = ParseTags(Tags);

        // Get or create up-down counter instrument
        var upDownCounter = AetherMeter.GetOrCreateUpDownCounter(
            metricName,
            unit,
            $"Number of in-progress executions of {GetMethodDisplayName(args)}");

        // Get or create invocation counter if enabled
        Counter<long>? invocationCounter = null;
        if (RecordInvocationCount)
        {
            var counterName = CreateMetricName(args, "invocations");
            invocationCounter = AetherMeter.GetOrCreateCounter(
                counterName,
                "invocations",
                $"Number of invocations of {GetMethodDisplayName(args)}");
        }

        // Get or create duration histogram if enabled
        Histogram<double>? durationHistogram = null;
        Stopwatch? stopwatch = null;
        if (RecordExecutionTime)
        {
            var durationName = CreateMetricName(args, "duration");
            durationHistogram = AetherMeter.GetOrCreateHistogram(
                durationName,
                "ms",
                $"Duration of {GetMethodDisplayName(args)}");
            stopwatch = Stopwatch.StartNew();
        }

        // Get or create error counter if enabled
        Counter<long>? errorCounter = null;
        if (RecordErrorCount)
        {
            var errorName = CreateMetricName(args, "errors");
            errorCounter = AetherMeter.GetOrCreateCounter(
                errorName,
                "errors",
                $"Number of errors in {GetMethodDisplayName(args)}");
        }

        try
        {
            // Increment in-progress counter
            upDownCounter.Add(1, tags);

            // Increment invocation counter if enabled
            invocationCounter?.Add(1, tags);

            // Call extensibility point before execution
            await OnBeforeAsync(args);

            // Execute the method
            await args.ProceedAsync();

            // Record duration if enabled
            if (stopwatch != null && durationHistogram != null)
            {
                stopwatch.Stop();
                durationHistogram.Record(stopwatch.ElapsedMilliseconds, tags);
            }

            // Call extensibility point after execution
            await OnAfterAsync(args);
        }
        catch (Exception ex)
        {
            // Record duration even on error if enabled
            if (stopwatch != null && durationHistogram != null)
            {
                stopwatch.Stop();
                var errorTags = CloneTags(tags);
                errorTags.Add("error", "true");
                errorTags.Add("exception.type", ex.GetType().Name);
                durationHistogram.Record(stopwatch.ElapsedMilliseconds, errorTags);
            }

            // Increment error counter
            var exceptionTags = CloneTags(tags);
            exceptionTags.Add("exception.type", ex.GetType().Name);
            errorCounter?.Add(1, exceptionTags);

            // Call extensibility point on exception
            await OnExceptionAsync(args, ex);

            // Re-throw the exception
            throw;
        }
        finally
        {
            // Always decrement in-progress counter
            upDownCounter.Add(-1, tags);
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates a metric name from the method signature following OpenTelemetry/Prometheus conventions.
    /// Format: {namespace}.{class}.{method}.{suffix}
    /// Example: bbt.aether.orderservice.processorder.duration
    /// </summary>
    private string CreateMetricName(MethodInterceptionArgs args, string suffix)
    {
        if (!string.IsNullOrEmpty(MetricName))
        {
            return MetricName;
        }

        var method = args.Method;
        var declaringType = method.DeclaringType;
        var namespacePart = declaringType?.Namespace?.ToLowerInvariant().Replace('.', '_') ?? "unknown";
        var className = declaringType?.Name.ToLowerInvariant() ?? "unknown";
        var methodName = method.Name.ToLowerInvariant();

        // Remove async suffix if present
        if (methodName.EndsWith("async"))
        {
            methodName = methodName.Substring(0, methodName.Length - 5);
        }

        return $"{namespacePart}.{className}.{methodName}.{suffix}";
    }

    /// <summary>
    /// Gets a display name for the method (for descriptions).
    /// </summary>
    private string GetMethodDisplayName(MethodInterceptionArgs args)
    {
        var method = args.Method;
        var className = method.DeclaringType?.Name ?? "Unknown";
        return $"{className}.{method.Name}";
    }

    /// <summary>
    /// Parses tags from "key:value" string array format to TagList.
    /// </summary>
    private TagList ParseTags(string[]? tagStrings)
    {
        var tagList = new TagList();

        if (tagStrings == null || tagStrings.Length == 0)
        {
            return tagList;
        }

        foreach (var tagString in tagStrings)
        {
            var parts = tagString.Split(':', 2);
            if (parts.Length == 2)
            {
                tagList.Add(parts[0].Trim(), parts[1].Trim());
            }
        }

        return tagList;
    }

    /// <summary>
    /// Clones a TagList and optionally adds additional tags.
    /// </summary>
    private TagList CloneTags(TagList source)
    {
        var clone = new TagList();
        foreach (var tag in source)
        {
            clone.Add(tag.Key, tag.Value);
        }
        return clone;
    }

    #endregion
}

