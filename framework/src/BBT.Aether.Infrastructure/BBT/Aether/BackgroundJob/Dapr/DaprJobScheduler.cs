using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;
using BBT.Aether.Telemetry;
using Dapr.Jobs;
using Dapr.Jobs.Models;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.BackgroundJob.Dapr;

/// <summary>
/// Dapr implementation of the job scheduler.
/// Provides integration with Dapr's distributed job scheduling capabilities.
/// </summary>
public class DaprJobScheduler(
    DaprJobsClient daprJobsClient,
    IEventSerializer eventSerializer,
    ILogger<DaprJobScheduler> logger)
    : IJobScheduler
{
    /// <inheritdoc/>
    public async Task ScheduleAsync(
        string handlerName,
        string jobName,
        string schedule,
        ReadOnlyMemory<byte> payload,
        JobScheduleFailurePolicy? failurePolicyOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(handlerName))
            throw new ArgumentNullException(nameof(handlerName));

        if (string.IsNullOrWhiteSpace(jobName))
            throw new ArgumentNullException(nameof(jobName));

        if (string.IsNullOrWhiteSpace(schedule))
            throw new ArgumentNullException(nameof(schedule));

        using var activity = StartSchedulerActivity("BackgroundJob.Schedule", handlerName, jobName);
        activity?.SetTag("job.schedule", schedule);

        try
        {
            var daprSchedule = ParseSchedule(schedule);

            // Deserialize the envelope to object so Dapr can serialize it properly
            // This prevents double-serialization (base64 string wrapping) by Dapr
            // Same pattern as used in DaprEventBus
            var envelopeObject = eventSerializer.Deserialize<object>(payload.Span);
            var payloadBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(envelopeObject);

            await daprJobsClient.ScheduleJobAsync(
                jobName: jobName,
                schedule: daprSchedule,
                payload: new ReadOnlyMemory<byte>(payloadBytes),
                overwrite: true,
                failurePolicyOptions: MapFailurePolicy(failurePolicyOptions),
                cancellationToken: cancellationToken);

            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to schedule Dapr job handler '{HandlerName}' with job name '{JobName}'", handlerName, jobName);
            RecordException(activity, ex);
            throw new InvalidOperationException($"Failed to schedule job handler '{handlerName}' with job name '{jobName}'.", ex);
        }
    }

    /// <inheritdoc/>
    public async Task ScheduleOneShotAsync(
        string handlerName,
        string jobName,
        DateTime dueAtUtc,
        ReadOnlyMemory<byte> payload,
        JobScheduleFailurePolicy? failurePolicy = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(handlerName))
            throw new ArgumentNullException(nameof(handlerName));

        if (string.IsNullOrWhiteSpace(jobName))
            throw new ArgumentNullException(nameof(jobName));

        var dueAt = new DateTimeOffset(DateTime.SpecifyKind(dueAtUtc, DateTimeKind.Utc));

        using var activity = StartSchedulerActivity("BackgroundJob.Schedule.OneShot", handlerName, jobName);
        activity?.SetTag("job.due_at", dueAt.ToString("O"));

        try
        {
            var daprSchedule = DaprJobSchedule.FromDateTime(dueAt);

            // Deserialize the envelope to object so Dapr can serialize it properly
            // This prevents double-serialization (base64 string wrapping) by Dapr
            // Same pattern as used in ScheduleAsync / DaprEventBus
            var envelopeObject = eventSerializer.Deserialize<object>(payload.Span);
            var payloadBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(envelopeObject);

            await daprJobsClient.ScheduleJobAsync(
                jobName: jobName,
                schedule: daprSchedule,
                payload: new ReadOnlyMemory<byte>(payloadBytes),
                overwrite: true,
                failurePolicyOptions: MapFailurePolicy(failurePolicy),
                cancellationToken: cancellationToken);

            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to schedule one-shot Dapr job handler '{HandlerName}' with job name '{JobName}'", handlerName, jobName);
            RecordException(activity, ex);
            throw new InvalidOperationException($"Failed to schedule one-shot job handler '{handlerName}' with job name '{jobName}'.", ex);
        }
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string handlerName, string jobName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(handlerName))
            throw new ArgumentNullException(nameof(handlerName));

        if (string.IsNullOrWhiteSpace(jobName))
            throw new ArgumentNullException(nameof(jobName));

        using var activity = StartSchedulerActivity("BackgroundJob.Schedule.Delete", handlerName, jobName);

        try
        {
            await daprJobsClient.DeleteJobAsync(
                jobName: jobName,
                cancellationToken: cancellationToken);

            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete Dapr job handler '{HandlerName}' with job name '{JobName}'", handlerName, jobName);
            RecordException(activity, ex);
            throw new InvalidOperationException($"Failed to delete job handler '{handlerName}' with job name '{jobName}'.", ex);
        }
    }

    private static Activity? StartSchedulerActivity(string operationName, string handlerName, string jobName)
    {
        var activity = InfrastructureActivitySource.Source.StartActivity(
            operationName,
            ActivityKind.Client,
            Activity.Current?.Context ?? default);

        activity?.SetTag("job.scheduler", "dapr");
        activity?.SetTag("job.handler_name", handlerName);
        activity?.SetTag("job.name", jobName);

        return activity;
    }

    private static void RecordException(Activity? activity, Exception ex)
    {
        if (activity == null) return;

        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            { "exception.type", ex.GetType().FullName ?? ex.GetType().Name },
            { "exception.message", ex.Message },
        }));
    }

    private static IJobFailurePolicyOptions? MapFailurePolicy(JobScheduleFailurePolicy? policy) =>
        policy switch
        {
            null => null,
            { PolicyType: FailurePolicyType.Drop } => new JobFailurePolicyDropOptions(),
            { PolicyType: FailurePolicyType.Constant, Interval: { } interval } =>
                new JobFailurePolicyConstantOptions(interval) { MaxRetries = policy.MaxRetries },
            _ => null
        };

    /// <summary>
    /// Parses a schedule string into a <see cref="DaprJobSchedule"/>.
    /// Distinguishes recurring cron / "@" period expressions, one-shot ISO-8601 instants,
    /// and duration / delay expressions. Unrecognized strings fall back to an expression.
    /// </summary>
    /// <param name="schedule">The schedule string to parse.</param>
    /// <returns>A <see cref="DaprJobSchedule"/> instance.</returns>
    private static DaprJobSchedule ParseSchedule(string schedule)
    {
        switch (DetectKind(schedule))
        {
            case ScheduleKind.Instant:
                // Already validated parseable by DetectKind; normalize to UTC.
                var instant = DateTimeOffset.Parse(
                    schedule,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
                return DaprJobSchedule.FromDateTime(instant);

            case ScheduleKind.Duration:
                return DaprJobSchedule.FromDuration(ParseDuration(schedule));

            case ScheduleKind.Cron:
            default:
                return DaprJobSchedule.FromExpression(schedule);
        }
    }

    /// <summary>
    /// Classifies a raw schedule string so the scheduling branch is unit-testable without a Dapr client.
    /// </summary>
    /// <param name="schedule">The schedule string to classify.</param>
    /// <returns>The detected <see cref="ScheduleKind"/>.</returns>
    internal static ScheduleKind DetectKind(string schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule))
            return ScheduleKind.Cron;

        var trimmed = schedule.Trim();

        // "@every 1h", "@daily", "@hourly", ... are recurring period expressions handled by Dapr's FromExpression.
        if (trimmed.StartsWith('@'))
            return ScheduleKind.Cron;

        // ISO-8601 instant (e.g. "2026-07-01T10:00:00Z"). Must contain a date separator to avoid
        // mis-classifying a bare TimeSpan ("00:00:30") as a DateTimeOffset.
        if (trimmed.Contains('-') && trimmed.Contains('T') &&
            DateTimeOffset.TryParse(
                trimmed,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out _))
        {
            return ScheduleKind.Instant;
        }

        // Duration / simple delay: ISO-8601 duration ("PT30S") or a TimeSpan ("00:00:30").
        if (TryParseDuration(trimmed, out _))
            return ScheduleKind.Duration;

        // Default: treat as a cron-like expression (preserves prior behavior).
        return ScheduleKind.Cron;
    }

    private static TimeSpan ParseDuration(string schedule) =>
        TryParseDuration(schedule.Trim(), out var duration)
            ? duration
            : throw new FormatException($"Schedule '{schedule}' is not a valid duration.");

    private static bool TryParseDuration(string value, out TimeSpan duration)
    {
        // ISO-8601 duration (e.g. "PT30S", "PT1H30M").
        if (value.StartsWith('P') || value.StartsWith('p'))
        {
            try
            {
                duration = System.Xml.XmlConvert.ToTimeSpan(value);
                return true;
            }
            catch (FormatException)
            {
                // Fall through to TimeSpan.TryParse.
            }
        }

        // Plain TimeSpan (e.g. "00:00:30"). Require a ':' so single integers aren't treated as durations.
        if (value.Contains(':') &&
            TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out duration))
        {
            return true;
        }

        duration = default;
        return false;
    }

    /// <summary>
    /// The classification of a schedule expression used to pick the appropriate Dapr trigger factory.
    /// </summary>
    internal enum ScheduleKind
    {
        /// <summary>Recurring cron expression or "@" prefixed period.</summary>
        Cron,

        /// <summary>One-shot fixed point in time (ISO-8601 instant).</summary>
        Instant,

        /// <summary>Duration / simple delay interval.</summary>
        Duration
    }
}
