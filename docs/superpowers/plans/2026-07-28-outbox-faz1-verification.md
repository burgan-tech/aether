# Faz 1 Outbox/Inbox Reliability — Deployment Verification Runbook

Companion to `2026-07-28-outbox-faz1-lease-fix-and-db-cost.md`. This runbook covers what to
capture immediately before rollout, what to check in the 24 hours after, and how to confirm the
partial dispatch index is actually being used by the planner.

## Pre-deploy baseline queries

Run these against production **before** deploying, so the fix can be proven afterwards. Both
queries should return a non-zero count pre-deploy (rows stranded by the dead reclaim-check bug)
and `0` after the fix has had time to run at least one lease cycle.

```sql
-- Rows stranded by the bug this phase fixes. Expected > 0 before deploy, 0 after.
SELECT count(*) FROM sys_queues."OutboxMessages" WHERE "Status" = 1 AND "LockedUntil" < now();
SELECT count(*) FROM sys_queues."InboxMessages"  WHERE "Status" = 1 AND "LockedUntil" < now();
```

**Enum divergence to keep in mind when writing any other ad-hoc query:**

| Value | Outbox | Inbox |
|-------|--------|-------|
| 0 | Pending | Pending |
| 1 | Processing | Processing |
| 2 | Processed | Processed |
| 3 | DeadLetter | Discarded |
| 4 | — | DeadLetter |

`Processing = 1` and `Processed = 2` are the same on both sides; the dead-letter/discard tail is
not — do not reuse a single `Status IN (...)` list across both tables without checking this table.

## Post-deploy checks (24h)

| Check | Success criterion |
|-------|--------------------|
| Stranded `Processing` count | `SELECT count(*) FROM sys_queues."OutboxMessages" WHERE "Status" = 1 AND "LockedUntil" < now();` (and the Inbox equivalent) reaches `0` and stays there |
| `Error processing outbox messages` log rate | Falls to under 10% of the pre-deploy rate |
| Dispatcher DB transactions/sec | Drops substantially — pre-deploy measurement was roughly 22 DB transactions per published message |
| `PartitionId` distribution | No longer uniformly `0` for newly written rows (see query below) |
| Index validity | Every `sys_queues` index reports `indisvalid = t` |

```sql
SELECT "PartitionId", count(*)
FROM sys_queues."OutboxMessages"
WHERE "CreatedAt" > now() - interval '1 hour'
GROUP BY 1
ORDER BY 2 DESC
LIMIT 10;
```

```sql
SELECT indexrelid::regclass AS idx, indisvalid
FROM pg_index
WHERE indrelid IN (
  'sys_queues."OutboxMessages"'::regclass,
  'sys_queues."InboxMessages"'::regclass
)
ORDER BY 1;
```

## Index plan check

Confirm the partial dispatch index is actually chosen by the planner, not a sequential scan:

```sql
EXPLAIN (ANALYZE, BUFFERS)
SELECT *
FROM sys_queues."OutboxMessages"
WHERE "Status" = ANY (ARRAY[0, 1])
ORDER BY "PartitionId", "NextRetryAt", "CreatedAt"
LIMIT 100;
```

Expect `IX_OutboxMessages_Dispatch` in the plan (Index Scan / Index Only Scan), not `Seq Scan`.

Note: the lease command is sent ad-hoc (not a prepared statement), so Postgres builds a custom
plan per execution and can see the literal `Status` values as constants — this is what lets the
planner match the partial index's `WHERE "Status" = ANY (ARRAY[0, 1])` predicate. If Npgsql
auto-prepare is ever enabled for this statement, re-check the plan: a prepared/generic plan may
stop matching the partial index predicate and silently fall back to a sequential scan.

## Rollback notes

- The migration's `Down` restores the old (non-partial, non-`PartitionId`-leading) indexes and
  drops the `PartitionId` column.
- `CREATE INDEX CONCURRENTLY` cannot run inside a transaction. If a rollback's index rebuild is
  interrupted, it can leave an index in `INVALID` state rather than cleanly failing.
- Before re-running any migration (forward or `Down`) after a failed concurrent index build,
  check `pg_index.indisvalid` and manually `DROP INDEX` any `INVALID` index first — retrying the
  same `CREATE INDEX CONCURRENTLY` without dropping the invalid one will error.

## Known deferred items (carried forward)

- **Dead-letter threshold mismatch**: the reclaim path uses `RetryCount > MaxRetryCount` while the
  publish-failure path uses `RetryCount + 1 >= MaxRetryCount`. Reviewed as bounded (both terminate,
  neither loops indefinitely) and intentionally left inconsistent-but-safe; tracked separately for
  a follow-up cleanup rather than blocking this phase.
- **Cleanup skipped on cycle failure**: `RunAsync` wraps both message processing and retention
  cleanup in a single try/catch, so a processing exception skips cleanup for that cycle too. Left
  alone deliberately — decoupling them would add a guaranteed extra DB connection attempt exactly
  when the database is already in distress, which is the wrong trade during an outage.
- **`MaxPollingInterval` stays at 60s** until a wake-up/interrupt signal exists for the poller; no
  change planned this phase.
- **Config delivery risk**: if the deployment's actual configuration lives in a separate
  infrastructure repo and sets `Aether__Outbox__*` / `Aether__Inbox__*` environment variable
  overrides, the appsettings changes shipped in this phase are inert there — the overrides must be
  applied at that layer, not assumed to take effect from the appsettings defaults alone.
