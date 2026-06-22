using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.Repositories;
using BBT.Aether.MultiSchema;
using BBT.Aether.Uow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.BackgroundJob.Processing;

/// <summary>
/// Arming poller for background jobs. Makes the job table the source of truth and eliminates orphaned
/// jobs: each run leases the schema's due jobs (status <see cref="BackgroundJobStatus.Pending"/>, or
/// <see cref="BackgroundJobStatus.Retrying"/> with a past <see cref="BackgroundJobInfo.NextRetryAt"/>),
/// arms them in the external scheduler OUTSIDE any transaction, and atomically flips each armed row to
/// <see cref="BackgroundJobStatus.Scheduled"/>. Mirrors the outbox processor's shape.
/// </summary>
public class BackgroundJobArmingProcessor(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    BackgroundJobOptions options,
    ILogger<BackgroundJobArmingProcessor> logger)
{
    /// <summary>
    /// Runs a single arming pass for the configured schema.
    /// </summary>
    public virtual async Task RunAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.Schema))
        {
            logger.LogWarning("Background-job arming processor has no Schema configured; skipping run.");
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var currentSchema = sp.GetRequiredService<ICurrentSchema>();
        var uowManager = sp.GetRequiredService<IUnitOfWorkManager>();
        var jobStore = sp.GetRequiredService<IJobStore>();
        var scheduler = sp.GetRequiredService<IJobScheduler>();

        using (currentSchema.Change(options.Schema))
        {
            // PHASE 1: read the due jobs in a short transaction.
            List<BackgroundJobInfo> due;
            await using (var readUow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                due = (await jobStore.GetDueForArmingAsync(clock.UtcNow, options.ArmingBatchSize, ct)).ToList();
                await readUow.CommitAsync(ct);
            }

            // PHASE 2: arm each job in the external scheduler (no open transaction), then flip its status.
            foreach (var job in due)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    // The stored Payload is the SerializeToElement(envelope) JsonElement that
                    // BackgroundJobService produced from the SAME envelope it serialized to bytes for the
                    // scheduler. IEventSerializer has no Serialize(JsonElement) overload, so reconstruct the
                    // identical wire bytes from the element's raw JSON text (UTF-8). The scheduler does
                    // Deserialize<object>(payload) then reserializes, so byte-for-byte equality is not
                    // required — equivalent JSON is sufficient, which GetRawText() guarantees.
                    var payloadBytes = Encoding.UTF8.GetBytes(job.Payload.GetRawText());

                    var fromStatus = job.Status; // Pending or Retrying

                    // Arm OUTSIDE any transaction (external call).
                    // Retrying → one-shot at NextRetryAt; else use the stored schedule expression.
                    if (fromStatus == BackgroundJobStatus.Retrying && job.NextRetryAt is { } dueAt)
                    {
                        await scheduler.ScheduleOneShotAsync(
                            job.HandlerName, job.JobName, dueAt, payloadBytes, cancellationToken: ct);
                    }
                    else
                    {
                        await scheduler.ScheduleAsync(
                            job.HandlerName, job.JobName, job.ExpressionValue, payloadBytes, cancellationToken: ct);
                    }

                    // Flip to Scheduled atomically (CAS from the status we observed).
                    await using var uow = uowManager.Begin(
                        new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
                    await jobStore.TryTransitionStatusAsync(
                        job.Id, fromStatus, BackgroundJobStatus.Scheduled, ct);
                    await uow.CommitAsync(ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Failed to arm background job '{JobName}' (id {JobId}); will retry next interval",
                        job.JobName, job.Id);
                }
            }

            // --- Reap stuck Running jobs (crashed/timed-out executions) ---
            List<BackgroundJobInfo> stale;
            await using (var readUow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                stale = (await jobStore.GetStaleRunningAsync(
                    clock.UtcNow - options.VisibilityTimeout, options.ArmingBatchSize, ct)).ToList();
                await readUow.CommitAsync(ct);
            }

            foreach (var job in stale)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await using var uow = uowManager.Begin(
                        new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
                    const string reason = "Reaped: execution exceeded the visibility timeout";
                    if (job.Kind == JobKind.Recurring)
                    {
                        await jobStore.MarkRecurringRanAsync(job.Id, clock.UtcNow, reason, ct);                 // → Scheduled (next cron tick)
                    }
                    else if (job.RetryCount + 1 <= job.MaxRetryCount)
                    {
                        await jobStore.MarkRetryingAsync(job.Id, clock.UtcNow, reason, ct);                     // → Retrying, NextRetryAt=now ⇒ arming poller re-arms
                    }
                    else
                    {
                        await jobStore.UpdateStatusAsync(job.Id, BackgroundJobStatus.Failed, clock.UtcNow,
                            "Reaped: retries exhausted after a stuck execution", ct);
                    }

                    await uow.CommitAsync(ct);
                    logger.LogWarning(
                        "Reaped stuck background job '{JobName}' (id {JobId}) after visibility timeout",
                        job.JobName, job.Id);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Failed to reap stuck background job '{JobName}' (id {JobId}); will retry next interval",
                        job.JobName, job.Id);
                }
            }
        }
    }
}
