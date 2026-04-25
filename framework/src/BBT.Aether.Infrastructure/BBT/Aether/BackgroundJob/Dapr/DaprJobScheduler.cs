using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;
using BBT.Aether.Telemetry;
using Dapr.Jobs;
using Dapr.Jobs.Models;
using Microsoft.Extensions.Logging;

#pragma warning disable CS0618 // Type or member is obsolete

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
    public async Task UpdateScheduleAsync(
        string handlerName,
        string jobName,
        string newSchedule,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(handlerName))
            throw new ArgumentNullException(nameof(handlerName));

        if (string.IsNullOrWhiteSpace(jobName))
            throw new ArgumentNullException(nameof(jobName));

        if (string.IsNullOrWhiteSpace(newSchedule))
            throw new ArgumentNullException(nameof(newSchedule));

        using var activity = StartSchedulerActivity("BackgroundJob.Schedule.Update", handlerName, jobName);
        activity?.SetTag("job.schedule", newSchedule);

        try
        {
            var jobInfo = await daprJobsClient.GetJobAsync(jobName, cancellationToken);
            await daprJobsClient.DeleteJobAsync(jobName, cancellationToken);
            await ScheduleAsync(handlerName, jobName, newSchedule, jobInfo.Payload, cancellationToken);

            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update Dapr job handler '{HandlerName}' with job name '{JobName}'", handlerName, jobName);
            RecordException(activity, ex);
            throw new InvalidOperationException($"Failed to update job handler '{handlerName}' with job name '{jobName}'.", ex);
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

    /// <summary>
    /// Parses a schedule string into a DaprJobSchedule.
    /// Supports cron expressions and simple delay formats.
    /// </summary>
    /// <param name="schedule">The schedule string to parse.</param>
    /// <returns>A DaprJobSchedule instance.</returns>
    private DaprJobSchedule ParseSchedule(string schedule)
    {
        // Default to treating as cron expression
        return DaprJobSchedule.FromExpression(schedule);
    }
}
