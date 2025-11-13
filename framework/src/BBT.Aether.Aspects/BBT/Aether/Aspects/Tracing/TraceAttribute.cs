using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using PostSharp.Aspects;
using PostSharp.Aspects.Dependencies;
using PostSharp.Extensibility;
using PostSharp.Serialization;

namespace BBT.Aether.Aspects;

/// <summary>
/// Aspect that integrates with OpenTelemetry to provide distributed tracing capabilities.
/// Supports creating spans, adding events, or enriching the current activity.
/// Requires "BBT.Aether.Aspects" ActivitySource to be registered in OpenTelemetry configuration.
/// Execution order: Runs FIRST (outermost layer) - creates span before other aspects.
/// </summary>
[PSerializable]
[MulticastAttributeUsage(
    MulticastTargets.Method,
    AllowMultiple = false,
    Inheritance = MulticastInheritance.Strict)]
public class TraceAttribute : AetherMethodInterceptionAspect
{
    /// <summary>
    /// Gets or sets the tracing mode.
    /// Default is Span (creates a new Activity/span).
    /// </summary>
    public TracingMode Mode { get; set; } = TracingMode.Span;

    /// <summary>
    /// Gets or sets the ActivityKind for span creation.
    /// Only applies when Mode is Span.
    /// Default is Internal.
    /// </summary>
    public ActivityKind Kind { get; set; } = ActivityKind.Internal;

    /// <summary>
    /// Gets or sets an optional operation name override.
    /// If not provided, the method signature will be used.
    /// </summary>
    public string? OperationName { get; set; }

    /// <summary>
    /// Gets or sets custom tags in "key:value" format.
    /// Example: new[] { "user.id:123", "tenant:acme" }
    /// </summary>
    public string[]? Tags { get; set; }

    /// <summary>
    /// Intercepts async method execution to add distributed tracing.
    /// </summary>
    public async override Task OnInvokeAsync(MethodInterceptionArgs args)
    {
        switch (Mode)
        {
            case TracingMode.Span:
                await ExecuteWithSpanAsync(args);
                break;
            case TracingMode.Event:
                await ExecuteWithEventAsync(args);
                break;
            case TracingMode.Enrich:
                await ExecuteWithEnrichAsync(args);
                break;
            default:
                throw new InvalidOperationException($"Unsupported TracingMode: {Mode}");
        }
    }

    /// <summary>
    /// Intercepts synchronous method execution to add distributed tracing.
    /// Bridges sync methods to async tracing implementation.
    /// </summary>
    public override void OnInvoke(MethodInterceptionArgs args)
    {
        // Bridge synchronous method to async tracing handling
        OnInvokeAsync(args).GetAwaiter().GetResult();
    }

    #region Span Mode

    private async Task ExecuteWithSpanAsync(MethodInterceptionArgs args)
    {
        var operationName = CreateOperationName(args);
        var tags = ParseTags(Tags);

        // Start a new activity (span)
        using var activity = AetherActivitySource.Source.StartActivity(
            operationName,
            Kind,
            Activity.Current?.Context ?? default);

        if (activity == null)
        {
            // ActivitySource is not enabled/registered, just execute the method
            await args.ProceedAsync();
            return;
        }

        try
        {
            // Add custom tags
            if (tags != null)
            {
                foreach (var tag in tags)
                {
                    activity.SetTag(tag.Key, tag.Value);
                }
            }

            // Add standard tags
            AddStandardTags(activity, args);

            // Call extensibility point before execution
            await OnBeforeAsync(args);

            // Execute the method
            await args.ProceedAsync();

            // Mark as successful
            activity.SetStatus(ActivityStatusCode.Ok);

            // Call extensibility point after execution
            await OnAfterAsync(args);
        }
        catch (Exception ex)
        {
            // Record exception and mark activity as error
            RecordException(activity, ex);
            activity.SetStatus(ActivityStatusCode.Error, ex.Message);

            // Call extensibility point on exception
            await OnExceptionAsync(args, ex);

            // Re-throw the exception
            throw;
        }
    }

    #endregion

    #region Event Mode

    private async Task ExecuteWithEventAsync(MethodInterceptionArgs args)
    {
        var currentActivity = Activity.Current;
        if (currentActivity == null)
        {
            // No active activity, just execute the method
            await args.ProceedAsync();
            return;
        }

        var operationName = CreateOperationName(args);
        var tags = ParseTags(Tags);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Add entry event
            var entryTags = new ActivityTagsCollection(tags ?? new List<KeyValuePair<string, object?>>()) { { "event.type", "method.entry" } };
            AddStandardTagsToCollection(entryTags, args);

            currentActivity.AddEvent(new ActivityEvent($"{operationName}.entry", tags: entryTags));

            // Call extensibility point before execution
            await OnBeforeAsync(args);

            // Execute the method
            await args.ProceedAsync();

            stopwatch.Stop();

            // Add exit event
            var exitTags = new ActivityTagsCollection(tags ??  new List<KeyValuePair<string, object?>>())
            {
                { "event.type", "method.exit" }, { "duration.ms", stopwatch.ElapsedMilliseconds }
            };
            currentActivity.AddEvent(new ActivityEvent($"{operationName}.exit", tags: exitTags));

            // Call extensibility point after execution
            await OnAfterAsync(args);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            // Add error event
            var errorTags = new ActivityTagsCollection(tags  ??  new List<KeyValuePair<string, object?>>())
            {
                { "event.type", "method.error" }, { "duration.ms", stopwatch.ElapsedMilliseconds }, { "exception.type", ex.GetType().FullName ?? ex.GetType().Name },
                { "exception.message", ex.Message }
            };

            currentActivity.AddEvent(new ActivityEvent($"{operationName}.error", tags: errorTags));

            // Call extensibility point on exception
            await OnExceptionAsync(args, ex);

            // Re-throw the exception
            throw;
        }
    }

    #endregion

    #region Enrich Mode

    private async Task ExecuteWithEnrichAsync(MethodInterceptionArgs args)
    {
        var currentActivity = Activity.Current;
        if (currentActivity == null)
        {
            // No active activity, just execute the method
            await args.ProceedAsync();
            return;
        }

        var tags = ParseTags(Tags);

        // Add custom tags
        if (tags != null)
        {
            foreach (var tag in tags)
            {
                currentActivity.SetTag(tag.Key, tag.Value);
            }
        }

        // Add standard tags
        AddStandardTags(currentActivity, args);

        try
        {
            // Call extensibility point before execution
            await OnBeforeAsync(args);

            // Execute the method
            await args.ProceedAsync();

            // Call extensibility point after execution
            await OnAfterAsync(args);
        }
        catch (Exception ex)
        {
            // Add exception information as tags
            currentActivity.SetTag("exception.type", ex.GetType().FullName ?? ex.GetType().Name);
            currentActivity.SetTag("exception.message", ex.Message);
            currentActivity.SetStatus(ActivityStatusCode.Error, ex.Message);

            // Call extensibility point on exception
            await OnExceptionAsync(args, ex);

            // Re-throw the exception
            throw;
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates an operation name from the method signature or uses the provided override.
    /// </summary>
    private string CreateOperationName(MethodInterceptionArgs args)
    {
        if (!string.IsNullOrEmpty(OperationName))
        {
            return OperationName;
        }

        var method = args.Method;
        var className = method.DeclaringType?.Name ?? "Unknown";
        var methodName = method.Name;

        if (method.IsGenericMethod)
        {
            var genericArgs = string.Join(",", method.GetGenericArguments().Select(t => t.Name));
            methodName = $"{methodName}<{genericArgs}>";
        }

        return $"{className}.{methodName}";
    }

    /// <summary>
    /// Parses tags from "key:value" string array format to KeyValuePair collection.
    /// </summary>
    private IEnumerable<KeyValuePair<string, object?>>? ParseTags(string[]? tagStrings)
    {
        if (tagStrings == null || tagStrings.Length == 0)
        {
            return null;
        }

        var tags = new List<KeyValuePair<string, object?>>();
        foreach (var tagString in tagStrings)
        {
            var parts = tagString.Split(':', 2);
            if (parts.Length == 2)
            {
                tags.Add(new KeyValuePair<string, object?>(parts[0].Trim(), parts[1].Trim()));
            }
        }

        return tags.Count > 0 ? tags : null;
    }

    /// <summary>
    /// Adds standard tags (method metadata) to an Activity.
    /// </summary>
    private void AddStandardTags(Activity activity, MethodInterceptionArgs args)
    {
        var method = args.Method;
        var declaringType = method.DeclaringType;

        activity.SetTag("code.function", method.Name);
        if (declaringType != null)
        {
            activity.SetTag("code.namespace", declaringType.Namespace ?? "");
            activity.SetTag("code.class", declaringType.Name);
            activity.SetTag("code.filepath", declaringType.FullName ?? "");
        }

        // Add parameter count as metadata
        var parameters = method.GetParameters();
        if (parameters.Length > 0)
        {
            activity.SetTag("code.parameter_count", parameters.Length);
        }
    }

    /// <summary>
    /// Adds standard tags to an ActivityTagsCollection (for events).
    /// </summary>
    private void AddStandardTagsToCollection(ActivityTagsCollection tags, MethodInterceptionArgs args)
    {
        var method = args.Method;
        var declaringType = method.DeclaringType;

        tags.Add("code.function", method.Name);
        if (declaringType != null)
        {
            tags.Add("code.namespace", declaringType.Namespace ?? "");
            tags.Add("code.class", declaringType.Name);
        }
    }

    /// <summary>
    /// Records exception information to the activity following OpenTelemetry semantic conventions.
    /// </summary>
    private void RecordException(Activity activity, Exception ex)
    {
        var tags = new ActivityTagsCollection
        {
            { "exception.type", ex.GetType().FullName ?? ex.GetType().Name },
            { "exception.message", ex.Message }
        };

        if (!string.IsNullOrEmpty(ex.StackTrace))
        {
            tags.Add("exception.stacktrace", ex.StackTrace);
        }

        activity.AddEvent(new ActivityEvent("exception", tags: tags));

        // Also set tags on the activity itself
        activity.SetTag("error", true);
        activity.SetTag("exception.type", ex.GetType().FullName ?? ex.GetType().Name);
        activity.SetTag("exception.message", ex.Message);
    }

    #endregion
}

