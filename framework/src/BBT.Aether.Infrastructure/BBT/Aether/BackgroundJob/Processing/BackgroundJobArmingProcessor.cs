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
/// jobs. Each run executes three phases plus two reapers:
/// <list type="number">
///   <item><b>Claim</b> — a short transaction leases a distinct slice of due rows
///     (status <see cref="BackgroundJobStatus.Pending"/>, or <see cref="BackgroundJobStatus.Retrying"/>
///     with a past <see cref="BackgroundJobInfo.NextRetryAt"/>) by stamping
///     <see cref="BackgroundJobInfo.ArmingToken"/>/<see cref="BackgroundJobInfo.ArmingUntil"/>. Multiple
///     pods receive disjoint slices via <c>FOR UPDATE SKIP LOCKED</c>.</item>
///   <item><b>Arm</b> — each claimed job is armed in the external scheduler OUTSIDE any transaction.</item>
///   <item><b>Confirm/Abort</b> — a short transaction per job atomically clears the arming token and
///     transitions the row to <see cref="BackgroundJobStatus.Scheduled"/> (armed) or back to its
///     original status (arm failed), guarded on the arming token.</item>
/// </list>
/// Reaper A resets expired arming claims left behind by crashed pods. Reaper B resets stale Running jobs
/// that exceeded the visibility timeout.
/// </summary>
public class BackgroundJobArmingProcessor(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    BackgroundJobOptions options,
    ILogger<BackgroundJobArmingProcessor> logger)
{
    private readonly string _workerId =
        $"{Environment.GetEnvironmentVariable("HOSTNAME") ?? Environment.MachineName}" +
        $"/{Environment.ProcessId}/{Guid.NewGuid():N}/arming";

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
        var leaseStore = sp.GetRequiredService<IJobArmingLeaseStore>();
        var scheduler = sp.GetRequiredService<IJobScheduler>();

        using (currentSchema.Change(options.Schema))
        {
            // PHASE 1 — CLAIM: atomically lease a distinct slice of due jobs (short transaction).
            IReadOnlyList<BackgroundJobArmingClaim> claims;
            try
            {
                await using var claimUow = uowManager.Begin(
                    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
                claims = await leaseStore.ClaimBatchAsync(
                    options.ArmingBatchSize, _workerId, options.ArmingLeaseDuration, ct);
                await claimUow.CommitAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to claim background jobs for arming; will retry next interval.");
                claims = Array.Empty<BackgroundJobArmingClaim>();
            }

            // PHASE 2 + 3 — ARM each claim outside any transaction, then CONFIRM/ABORT in a short tx.
            foreach (var claim in claims)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                var job = claim.Job;
                bool armed;

                // PHASE 2 — ARM (external call, no open transaction).
                try
                {
                    // The stored Payload is the SerializeToElement(envelope) JsonElement that
                    // BackgroundJobService produced from the SAME envelope it serialized to bytes for the
                    // scheduler. IEventSerializer has no Serialize(JsonElement) overload, so reconstruct the
                    // identical wire bytes from the element's raw JSON text (UTF-8). The scheduler does
                    // Deserialize<object>(payload) then reserializes, so byte-for-byte equality is not
                    // required — equivalent JSON is sufficient, which GetRawText() guarantees.
                    var payloadBytes = Encoding.UTF8.GetBytes(job.Payload.GetRawText());

                    // Retrying → one-shot at NextRetryAt; else use the stored schedule expression.
                    if (claim.OriginalStatus == BackgroundJobStatus.Retrying && job.NextRetryAt is { } dueAt)
                    {
                        await scheduler.ScheduleOneShotAsync(
                            job.HandlerName, job.JobName, dueAt, payloadBytes, cancellationToken: ct);
                    }
                    else
                    {
                        await scheduler.ScheduleAsync(
                            job.HandlerName, job.JobName, job.ExpressionValue, payloadBytes, cancellationToken: ct);
                    }

                    armed = true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Shutting down: stop processing further claims. The claim's lease will expire and
                    // Reaper A returns the row to Pending.
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Failed to arm background job '{JobName}' (id {JobId}); reverting to {OriginalStatus}.",
                        job.JobName, job.Id, claim.OriginalStatus);
                    armed = false;
                }

                // PHASE 3 — CONFIRM (armed → Scheduled) or ABORT (failed → original status).
                // Both paths atomically clear ArmingToken/ArmingUntil, guarded on the arming token and
                // the status observed when the claim was acquired.
                var targetStatus = armed ? BackgroundJobStatus.Scheduled : claim.OriginalStatus;
                var inspectLostArmedClaim = false;
                try
                {
                    bool transitioned;
                    await using (var uow = uowManager.Begin(
                        new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
                    {
                        transitioned = await jobStore.TryTransitionFromArmingAsync(
                            job.Id, claim.ArmingToken, claim.OriginalStatus, targetStatus, ct);
                        await uow.CommitAsync(ct);
                    }

                    if (!transitioned)
                    {
                        logger.LogDebug(
                            "Job '{JobName}' (id {JobId}) arming claim no longer matched — another instance or cancellation acted on it.",
                            job.JobName, job.Id);
                        inspectLostArmedClaim = armed;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Failed to finalize arming for background job '{JobName}' (id {JobId}); the claim lease will expire and the reaper will reset it.",
                        job.JobName, job.Id);
                    inspectLostArmedClaim = armed;
                }

                if (inspectLostArmedClaim)
                {
                    await CompensateTerminalArmingLossAsync(
                        uowManager, jobStore, scheduler, job, claim.ArmingToken, ct);
                }
            }

            // REAPER A — reset expired arming claims left by crashed pods.
            try
            {
                await using var reaperUow = uowManager.Begin(
                    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
                var reset = await jobStore.ResetExpiredArmingClaimsAsync(
                    clock.UtcNow, options.ArmingBatchSize, ct);
                await reaperUow.CommitAsync(ct);

                if (reset > 0)
                {
                    logger.LogWarning(
                        "Reset {Count} expired arming claim(s) back to Pending (claiming pod presumed crashed).",
                        reset);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to reset expired arming claims; will retry next interval.");
            }

            // REAPER B — reap stuck Running jobs (crashed/timed-out executions).
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
                    // Guard every reaper transition on the token we observed: if the original (slow) execution
                    // records its outcome first, our guarded update affects 0 rows and we leave its state intact.
                    var token = job.RunningToken ?? Guid.Empty;
                    bool reaped;
                    if (job.Kind == JobKind.Recurring)
                    {
                        reaped = await jobStore.TryReturnToScheduledAsync(job.Id, token, clock.UtcNow, reason, ct);  // → Scheduled (next cron tick)
                    }
                    else if (job.RetryCount + 1 <= job.MaxRetryCount)
                    {
                        reaped = await jobStore.TryMarkRetryingAsync(job.Id, token, clock.UtcNow, reason, ct);       // → Retrying, NextRetryAt=now ⇒ arming poller re-arms
                    }
                    else
                    {
                        reaped = await jobStore.TryRecordTerminalAsync(job.Id, token, BackgroundJobStatus.Failed, clock.UtcNow,
                            "Reaped: retries exhausted after a stuck execution", ct);
                    }

                    await uow.CommitAsync(ct);
                    if (reaped)
                    {
                        logger.LogWarning(
                            "Reaped stuck background job '{JobName}' (id {JobId}) after visibility timeout",
                            job.JobName, job.Id);
                    }
                    else
                    {
                        // The original (slow) execution recorded its outcome first and still holds the claim —
                        // our guarded update matched no row. Nothing was reaped; leave its state untouched.
                        logger.LogDebug(
                            "Skipped reaping background job '{JobName}' (id {JobId}); the original execution recorded its outcome first",
                            job.JobName, job.Id);
                    }
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

    private async Task CompensateTerminalArmingLossAsync(
        IUnitOfWorkManager uowManager,
        IJobStore jobStore,
        IJobScheduler scheduler,
        BackgroundJobInfo claimedJob,
        Guid lostArmingToken,
        CancellationToken cancellationToken)
    {
        BackgroundJobCancellationSnapshot? snapshot;
        try
        {
            await using var readUow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
            snapshot = await jobStore.GetCancellationSnapshotAsync(claimedJob.Id, cancellationToken);
            await readUow.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to inspect lost arming claim for background job '{JobName}' (id {JobId}); compensating scheduler deletion was skipped.",
                claimedJob.JobName, claimedJob.Id);
            return;
        }

        var terminalWinner = snapshot?.Status is BackgroundJobStatus.Completed
            or BackgroundJobStatus.Failed
            or BackgroundJobStatus.Cancelled;

        if (!terminalWinner)
            return;

        var compensationToken = Guid.NewGuid();
        var now = clock.UtcNow;
        var compensationLeaseDuration = options.ArmingLeaseDuration > TimeSpan.Zero
            ? options.ArmingLeaseDuration
            : TimeSpan.FromSeconds(30);
        bool acquired;
        try
        {
            await using var acquireUow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
            acquired = await jobStore.TryAcquireTerminalArmingCompensationAsync(
                claimedJob.Id,
                lostArmingToken,
                compensationToken,
                now,
                now.Add(compensationLeaseDuration),
                cancellationToken);
            await acquireUow.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to acquire compensating scheduler-cleanup lease for background job '{JobName}' (id {JobId}); deletion was skipped.",
                snapshot!.JobName, claimedJob.Id);
            return;
        }

        if (!acquired)
            return;

        using var deleteCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewalTask = RenewCompensationLeaseUntilCancelledAsync(
            uowManager,
            jobStore,
            claimedJob,
            compensationToken,
            compensationLeaseDuration,
            deleteCancellation);
        try
        {
            await scheduler.DeleteAsync(
                snapshot!.HandlerName, snapshot.JobName, deleteCancellation.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed compensating scheduler deletion for terminal background job '{JobName}' (id {JobId}); persisted state remains unchanged.",
                snapshot!.JobName, claimedJob.Id);
        }
        finally
        {
            try
            {
                try
                {
                    await deleteCancellation.CancelAsync();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Failed to stop compensation-lease renewal for background job '{JobName}' (id {JobId}); release will still be attempted.",
                        snapshot!.JobName, claimedJob.Id);
                }

                try
                {
                    await renewalTask;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Compensation-lease renewal ended with an error for background job '{JobName}' (id {JobId}); release will still be attempted.",
                        snapshot!.JobName, claimedJob.Id);
                }
            }
            finally
            {
                try
                {
                    await using var releaseUow = uowManager.Begin(
                        new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
                    var released = await jobStore.TryReleaseArmingCompensationAsync(
                        claimedJob.Id, compensationToken, CancellationToken.None);
                    await releaseUow.CommitAsync(CancellationToken.None);

                    if (!released)
                    {
                        logger.LogWarning(
                            "Compensating scheduler-cleanup lease for background job '{JobName}' (id {JobId}) was no longer owned during release.",
                            snapshot!.JobName, claimedJob.Id);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Failed to release compensating scheduler-cleanup lease for background job '{JobName}' (id {JobId}); the arming reaper will recover it after expiry.",
                        snapshot!.JobName, claimedJob.Id);
                }
            }
        }
    }

    private async Task RenewCompensationLeaseUntilCancelledAsync(
        IUnitOfWorkManager uowManager,
        IJobStore jobStore,
        BackgroundJobInfo claimedJob,
        Guid compensationToken,
        TimeSpan leaseDuration,
        CancellationTokenSource deleteCancellation)
    {
        var renewalInterval = TimeSpan.FromTicks(
            Math.Max(TimeSpan.TicksPerMillisecond, leaseDuration.Ticks / 3));

        try
        {
            while (true)
            {
                await Task.Delay(renewalInterval, deleteCancellation.Token);
                var renewalNow = clock.UtcNow;
                await using var renewUow = uowManager.Begin(
                    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
                var renewed = await jobStore.TryRenewArmingCompensationAsync(
                    claimedJob.Id,
                    compensationToken,
                    renewalNow.Add(leaseDuration),
                    deleteCancellation.Token);
                await renewUow.CommitAsync(deleteCancellation.Token);

                if (renewed)
                    continue;

                logger.LogWarning(
                    "Lost compensating scheduler-cleanup lease for background job '{JobName}' (id {JobId}); cancelling the stale delete.",
                    claimedJob.JobName, claimedJob.Id);
                await deleteCancellation.CancelAsync();
                return;
            }
        }
        catch (OperationCanceledException) when (deleteCancellation.IsCancellationRequested)
        {
            // Normal shutdown: delete completed, caller cancelled, or ownership was lost.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to renew compensating scheduler-cleanup lease for background job '{JobName}' (id {JobId}); cancelling the stale delete.",
                claimedJob.JobName, claimedJob.Id);
            await deleteCancellation.CancelAsync();
        }
    }
}
