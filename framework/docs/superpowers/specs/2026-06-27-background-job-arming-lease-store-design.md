# Background Job Arming Lease Store — Design Spec

**Date:** 2026-06-27
**Branch:** feature/multi-schema-jobs-inbox-outbox

---

## Problem

`BackgroundJobArmingHostedService` is registered on every pod. Each pod's timer tick calls
`BackgroundJobArmingProcessor.RunAsync`, which starts with a plain `SELECT` (no row-level locking)
from `GetDueForArmingAsync`. Under high load with 10–20 pods:

- Every pod reads the **same batch** of due jobs simultaneously.
- Every pod calls `scheduler.ScheduleAsync` for the same jobs (unnecessary Dapr traffic).
- The only guard is a CAS (`TryTransitionStatusAsync`) whose `bool` return value is silently
  discarded — no log, no abort of the redundant scheduler call.

## Goal

Each pod claims a **non-overlapping batch** atomically. Only one pod arms a given job in Dapr per
arming tick. Mirror the Outbox lease pattern (`IOutboxLeaseStore` / `NpgsqlOutboxLeaseStore`).

---

## Architecture

```
IJobArmingLeaseStore  (Infrastructure layer — provider-agnostic contract)
    ├── EfCoreJobArmingLeaseStore<TDbContext>  (Infrastructure — SQL Server fallback, low concurrency)
    └── NpgsqlJobArmingLeaseStore<TDbContext>  (Npgsql — FOR UPDATE SKIP LOCKED, full multi-pod isolation)

BackgroundJobArmingProcessor
    Phase 1 — Claim:    IJobArmingLeaseStore.ClaimBatchAsync()       → unique batch per pod
    Phase 2 — Arm:      IJobScheduler.ScheduleAsync()                → single call per job
    Phase 3 — Confirm:  IJobStore.TryTransitionFromArmingAsync()     → Scheduled or revert
    Reaper  — Cleanup:  IJobStore.ResetExpiredArmingClaimsAsync()    → unstick crashed claims
```

---

## Entity Changes — `BackgroundJobInfo`

Two new nullable columns, mirroring `RunningToken`/`RunningSince`:

| Column | Type | Description |
|--------|------|-------------|
| `ArmingToken` | `Guid?` | Opaque claim token; `null` = row is available |
| `ArmingUntil` | `DateTime?` | UTC lease expiry; reaper resets rows past this time |

`GetDueForArmingAsync` WHERE clause gains an extra predicate:
```sql
AND (ArmingToken IS NULL OR ArmingUntil < @now)
```

This ensures rows being actively claimed by another pod are excluded from the plain EF query
(belt-and-suspenders with `SKIP LOCKED`, also correct for the SQL Server fallback).

---

## New Interface: `IJobArmingLeaseStore`

Location: `BBT.Aether.Infrastructure/BBT/Aether/BackgroundJob/`

```csharp
public interface IJobArmingLeaseStore
{
    /// <summary>
    /// Atomically claims up to <paramref name="batchSize"/> due jobs for this worker.
    /// Sets ArmingToken + ArmingUntil on each claimed row.
    /// </summary>
    Task<IReadOnlyList<BackgroundJobArmingClaim>> ClaimBatchAsync(
        int batchSize,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);
}

/// <summary>Represents a single job claim returned by ClaimBatchAsync.</summary>
public record BackgroundJobArmingClaim(
    BackgroundJobInfo Job,
    BackgroundJobStatus OriginalStatus,
    Guid ArmingToken);
```

`OriginalStatus` is captured at claim time so Phase 3 can revert on failure without re-reading.

---

## Implementations

### `EfCoreJobArmingLeaseStore<TDbContext>` (Infrastructure — SQL Server fallback)

Uses `ExecuteUpdateAsync` with a `WHERE ArmingToken IS NULL OR ArmingUntil < now` guard.
No row-level locking — suitable for single-pod or low-concurrency SQL Server deployments.
Multiple pods may occasionally claim the same row; the Phase 3 token guard resolves conflicts.

### `NpgsqlJobArmingLeaseStore<TDbContext>` (Npgsql — full multi-pod isolation)

Issues a single `UPDATE … WHERE id IN (SELECT … FOR UPDATE SKIP LOCKED) RETURNING *` statement
via raw ADO.NET, identical in shape to `NpgsqlOutboxLeaseStore`. Handles `SET LOCAL search_path`
injection for search-path-mode schemas.

```sql
UPDATE "BackgroundJobs"
SET
    "ArmingToken" = @armingToken,
    "ArmingUntil" = @armingUntil
WHERE "Id" IN (
    SELECT "Id"
    FROM "BackgroundJobs"
    WHERE ("Status" = @pending
           OR ("Status" = @retrying AND "NextRetryAt" IS NOT NULL AND "NextRetryAt" <= @now))
      AND ("ArmingToken" IS NULL OR "ArmingUntil" < @now)
    ORDER BY "NextRetryAt" NULLS FIRST, "Id"
    LIMIT @batchSize
    FOR UPDATE SKIP LOCKED
)
RETURNING "Id", "Status", "HandlerName", "JobName", "ExpressionValue", "Payload",
          "RetryCount", "NextRetryAt", "Kind", "MaxRetryCount", "ArmingToken", "ArmingUntil";
```

Each pod receives a non-overlapping set of rows. If one pod crashes mid-arming, the reaper
reclaims rows whose `ArmingUntil` has passed.

---

## `IJobStore` — Two New Methods

```csharp
/// <summary>
/// Clears ArmingToken/ArmingUntil and transitions the job to <paramref name="to"/>,
/// guarded by the arming token. Returns false if the token no longer matches (another
/// pod already acted on this row).
/// </summary>
Task<bool> TryTransitionFromArmingAsync(
    Guid id,
    Guid armingToken,
    BackgroundJobStatus to,
    CancellationToken cancellationToken = default);

/// <summary>
/// Bulk-resets all rows whose ArmingUntil < <paramref name="now"/> back to Pending.
/// Called by the reaper to unstick jobs from a crashed arming pod.
/// </summary>
Task<int> ResetExpiredArmingClaimsAsync(
    DateTime now,
    int batchSize,
    CancellationToken cancellationToken = default);
```

`TryTransitionFromArmingAsync` replaces the existing silent `TryTransitionStatusAsync` call in
the arming loop (whose `bool` return was discarded).

`ResetExpiredArmingClaimsAsync` does not need a token guard — if `ArmingUntil` has passed,
the original claimant is gone.

---

## `BackgroundJobArmingProcessor` — Revised Flow

```
Phase 1 — Claim (short transaction)
    var claims = await leaseStore.ClaimBatchAsync(
        options.ArmingBatchSize, workerId, options.ArmingLeaseDuration, ct);
    // Each pod receives a distinct slice. Empty → nothing to do this tick.

Phase 2 — Arm (no open transaction)
    foreach (var claim in claims)
        await scheduler.ScheduleAsync(...) / ScheduleOneShotAsync(...)

Phase 3 — Confirm or Abort (short transaction per job)
    success → TryTransitionFromArmingAsync(id, token, Scheduled)
    failure → TryTransitionFromArmingAsync(id, token, claim.OriginalStatus)
    // Both paths clear ArmingToken + ArmingUntil atomically.
    // If another pod already acted (token mismatch) → log Debug, continue.

Reaper (extended — runs after the arming loop)
    await jobStore.ResetExpiredArmingClaimsAsync(clock.UtcNow, options.ArmingBatchSize, ct);
    // Unsticks jobs claimed by a pod that crashed between Phase 1 and Phase 3.
```

The existing `GetStaleRunningAsync` reaper loop is unchanged.

---

## `BackgroundJobOptions` — New Field

```csharp
/// <summary>
/// How long an arming claim is held before the reaper treats the claiming pod as crashed
/// and releases the claim. Must be comfortably longer than a single arming pass.
/// </summary>
public TimeSpan ArmingLeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
```

Default 30s: 3× the default `ArmingInterval` of 10s, short enough for fast recovery.

---

## `WorkerIdentity` — Shared Registration

`WorkerIdentity` is currently registered only by `AddAetherOutbox`. It must also be registered by
`AddAetherBackgroundJob` (using `TryAddSingleton`) so that deployments without the Outbox package
still get a stable pod identity for the arming lease store.

---

## DI Wiring

### `AddAetherBackgroundJob<TDbContext>` (Infrastructure)

```csharp
services.TryAddSingleton<WorkerIdentity>();
services.TryAddScoped<IJobArmingLeaseStore, EfCoreJobArmingLeaseStore<TDbContext>>();
```

### `AddAetherNpgsql<TDbContext>` (Npgsql)

```csharp
if (typeof(IHasEfCoreBackgroundJobs).IsAssignableFrom(typeof(TDbContext)))
    services.AddScoped(
        typeof(IJobArmingLeaseStore),
        typeof(NpgsqlJobArmingLeaseStore<>).MakeGenericType(typeof(TDbContext)));
```

`AddScoped` (not `TryAddScoped`) intentionally overrides the EF Core fallback registered above.

---

## Model Builder Changes

```csharp
entity.Property(e => e.ArmingToken);
entity.Property(e => e.ArmingUntil);

// Partial index — only covers rows that are actively claimed; keeps index small.
entity.HasIndex(e => e.ArmingUntil)
    .HasFilter("\"ArmingToken\" IS NOT NULL")
    .HasDatabaseName("IX_BackgroundJobs_ArmingUntil");
```

The existing `IX_BackgroundJobs_Arming` index on `(Status, NextRetryAt)` remains; the new index
serves the reaper's `WHERE ArmingToken IS NOT NULL AND ArmingUntil < @now` scan.

---

## Migration

Two nullable columns and one partial index added to `BackgroundJobs`:

```sql
ALTER TABLE "BackgroundJobs" ADD "ArmingToken"  uuid               NULL;
ALTER TABLE "BackgroundJobs" ADD "ArmingUntil"  timestamp with time zone NULL;

CREATE INDEX "IX_BackgroundJobs_ArmingUntil"
    ON "BackgroundJobs" ("ArmingUntil")
    WHERE "ArmingToken" IS NOT NULL;
```

No data migration needed — all existing rows start with `NULL` columns, which means "available".

---

## Files Changed / Created

| File | Action |
|------|--------|
| `BBT.Aether.Domain/.../BackgroundJobInfo.cs` | Add `ArmingToken`, `ArmingUntil` properties |
| `BBT.Aether.Infrastructure/.../IJobArmingLeaseStore.cs` | New interface + `BackgroundJobArmingClaim` record |
| `BBT.Aether.Infrastructure/.../EfCoreJobArmingLeaseStore.cs` | New — EF Core fallback |
| `BBT.Aether.Infrastructure/.../IJobStore.cs` | Add `TryTransitionFromArmingAsync`, `ResetExpiredArmingClaimsAsync` |
| `BBT.Aether.Infrastructure/.../EfCoreJobStore.cs` | Implement the two new methods; update `GetDueForArmingAsync` filter |
| `BBT.Aether.Infrastructure/.../BackgroundJobArmingProcessor.cs` | Rewrite arming loop (3-phase) |
| `BBT.Aether.Infrastructure/.../BackgroundJobModelBuilderExtensions.cs` | Add 2 properties + partial index |
| `BBT.Aether.Infrastructure/.../AetherBackgroundJobServiceCollectionExtensions.cs` | Register `WorkerIdentity` + `EfCoreJobArmingLeaseStore` |
| `BBT.Aether.Npgsql/.../NpgsqlJobArmingLeaseStore.cs` | New — `FOR UPDATE SKIP LOCKED` implementation |
| `BBT.Aether.Npgsql/.../AetherNpgsqlServiceCollectionExtensions.cs` | Override `IJobArmingLeaseStore` with Npgsql variant |
| Migration file | Add `ArmingToken`, `ArmingUntil`, `IX_BackgroundJobs_ArmingUntil` |

---

## Out of Scope

- SQL Server `UPDLOCK READPAST` optimization (deferred, consistent with inbox/outbox SQL Server deferral)
- Changes to `BackgroundJobArmingHostedService` itself (no changes needed)
- Changes to the Running-phase reaper (existing `GetStaleRunningAsync` loop unchanged)
