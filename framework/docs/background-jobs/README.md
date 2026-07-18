# Background Jobs

## Overview

The background-job system runs scheduled and recurring work with the **job table as the single source of
truth**. By default (`directly: false`), enqueue writes a database row only and a fixed-schema **arming poller**
later registers the job in an external scheduler (default: Dapr Jobs). With `directly: true`, enqueue attempts
to register the schedule only after the row's persistence transaction commits. The scheduler's fire is routed
back through an execution bridge to a type-safe handler. This row-first design makes enqueue atomic with the
caller's business transaction (no orphaned schedules on rollback), provides framework-managed retry with
exponential backoff for one-shot jobs, and uses an optimistic-concurrency claim so delivery is at-least-once
safe.

The pieces:

| Component | Role |
|-----------|------|
| `IBackgroundJobService` (`BackgroundJobService`) | `EnqueueAsync` / `UpdateAsync` / `DeleteAsync`. The default enqueue path writes the row only; `directly: true` attempts to arm the scheduler after persistence commits. |
| `BackgroundJobArmingProcessor` | Fixed-schema poller. Leases due rows, arms them in the scheduler **outside** any transaction, then flips them to `Scheduled`. |
| `IJobScheduler` (`DaprJobScheduler`) | The external scheduler abstraction (`ScheduleAsync`, `ScheduleOneShotAsync`, `DeleteAsync`). |
| `IJobExecutionBridge` (`DaprJobExecutionBridge`) | Dapr trigger entry point: looks the job up (in its own UoW) and delegates to the dispatcher. Does no other DB work. |
| `IJobDispatcher` (`JobDispatcher`) | CAS-claims `Scheduled→Running` (stamping `RunningSince`), invokes the handler with **no dispatcher-owned transaction**, records the outcome. |
| `IJobStore` (`EfCoreJobStore<TDbContext>`) | EF Core persistence + the atomic status transitions. |

## Lifecycle

```
       EnqueueAsync default/directly:false (writes row, no scheduler call)
                         │
                         ▼
                     ┌─────────┐
                     │ Pending │
                     └─────────┘
                         │  arming poller: ScheduleAsync / ScheduleOneShotAsync
                         ▼  (then CAS Pending→Scheduled)
                     ┌───────────┐
                     │ Scheduled │◀───────────────────────────────┐
                     └───────────┘                                │
                         │  Dapr fires → bridge → dispatcher       │
                         ▼  (CAS Scheduled→Running, stamp          │
                     ┌─────────┐    RunningSince = now)            │
                     │ Running │                                   │
                     └─────────┘                                   │
            ┌────────────┼─────────────────────────┐              │
   success  │            │ one-shot failure         │ recurring   │
            ▼            ▼   (retries left)          │ (success or │
    ┌───────────┐  ┌──────────┐                     │  failure)   │
    │ Completed │  │ Retrying │── poller re-arms ────┘ ────────────┘
    │ (one-shot)│  └──────────┘   (one-shot at NextRetryAt)
    └───────────┘       │
        │               │ retries exhausted
        │               ▼
        │          ┌────────┐
        │          │ Failed │
        │          └────────┘
        └─ scheduler entry deleted        (Failed also deletes the scheduler entry)

  Reaper (arming processor): a job stuck in Running past VisibilityTimeout is reset —
      Running --(visibility timeout)--> Retrying   (one-shot, retries left → re-armed)
      Running --(visibility timeout)--> Scheduled  (recurring → re-armed)
      Running --(visibility timeout)--> Failed     (one-shot, retries exhausted)
```

Outcome rules recorded by the dispatcher:

- **One-shot success** → `Completed`, scheduler entry deleted.
- **One-shot failure, retries left** → `Retrying`, `RetryCount++`, `NextRetryAt = now + backoff`. **Not**
  deleted — the poller re-arms it as a one-shot at `NextRetryAt`.
- **One-shot failure, retries exhausted** → `Failed`, scheduler entry deleted.
- **Recurring success** → back to `Scheduled`, `LastRunAt` set, **not** deleted (stays armed for the next cron tick).
- **Recurring failure** → back to `Scheduled`, `RetryCount++`, error recorded, **not** deleted. Recurring
  jobs do **not** use framework retry/backoff — they simply wait for the next cron occurrence.
- **Cancellation** → `Cancelled`, scheduler entry deleted.

Every recorded outcome (and every reaper reset) clears `RunningSince` and `RunningToken`; the dispatcher
stamps both on the `Scheduled→Running` claim and the row is no longer "running" once an outcome lands. See
[Reaper / visibility timeout](#reaper--visibility-timeout) for what happens when an outcome never lands.

Statuses: `Pending`, `Scheduled`, `Running`, `Retrying`, `Completed`, `Failed`, `Cancelled`.
Job kinds: `JobKind.OneShot`, `JobKind.Recurring`.

## Registration

### DbContext

The DbContext must implement `IHasEfCoreBackgroundJobs` and configure the job entity:

```csharp
public class MyDbContext : AetherDbContext<MyDbContext>, IHasEfCoreBackgroundJobs
{
    public DbSet<BackgroundJobInfo> BackgroundJobs { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ConfigureBackgroundJob();
    }
}
```

### Services

`AddAetherBackgroundJob<TDbContext>(configure)` registers the core (scheduler-agnostic) services and reads
options from the `configure` callback. Chain a scheduler-specific extension such as `AddDaprJobScheduler()`.

```csharp
services.AddAetherBackgroundJob<MyDbContext>(o =>
{
    o.Schema = "sys_queues"; // fixed persistence schema for the dedicated BackgroundJob DbContext
    o.MaxRetryCount = 3;                          // one-shot framework retries
    o.RetryBaseDelay = TimeSpan.FromSeconds(30);  // exponential backoff base
    o.ArmingInterval = TimeSpan.FromSeconds(10);  // poller interval
    o.ArmingBatchSize = 100;                       // max jobs armed per pass
    o.AddHandler<SendEmailJobHandler>();           // handler name defaults to the type name
    o.AddHandler<GenerateReportJobHandler>("GenerateReport"); // or supply an explicit name
})
.AddDaprJobScheduler();
```

`AddHandler<THandler>(string? handlerName = null)` registers the handler in DI and, at startup, builds a
type-closed `IBackgroundJobInvoker` keyed by the handler name (no runtime reflection). The `handlerName` you
pass here is the same string you pass to `EnqueueAsync`.

`AddAetherBackgroundJob` also registers `BackgroundJobArmingProcessor` plus a hosted service
(`BackgroundJobArmingHostedService`) that drives `RunAsync` on the `ArmingInterval`. Hosted services run only
inside a built `Host`, so direct DI construction in tests never auto-starts the loop.

### Application (Dapr trigger endpoint)

Map the Dapr scheduled-job endpoint so Dapr fires route to the execution bridge:

```csharp
var app = builder.Build();
app.UseDaprScheduledJobHandler(); // maps MapDaprScheduledJobHandler → IJobExecutionBridge.ExecuteAsync
app.Run();
```

The endpoint catches only infrastructure faults and rethrows them (so Dapr observes a non-200 and applies its
own delivery retry). Handler failures never reach it: the dispatcher records the outcome and returns
normally, so Dapr sees a 200 and does **not** retry — framework retry owns handler retries.

### Fixed persistence schema

One `AddAetherBackgroundJob<TDbContext>` registration owns one immutable persistence schema. `Schema` binds
the dedicated BackgroundJob DbContext, store, and arming lease to that schema (for example, `sys_queues`).
Request-time tenant changes through `ICurrentSchema` never redirect `BackgroundJobs` into a tenant schema;
the store temporarily enters the configured persistence schema for each operation and restores the caller's
tenant schema before returning.

The serialized job payload independently carries the tenant schema that was active at enqueue time. The job
row therefore stays in the fixed persistence schema while dispatch restores the payload's tenant schema for
handler execution. If `Schema` is null or empty, persistence retains the historical ambient-schema behavior;
the arming processor itself requires a configured schema and otherwise logs a warning and does no work.

Schema selection for migrations remains model-driven. Generate migrations from the dedicated BackgroundJob
DbContext and keep its configured/default schema aligned with the persistence schema; request-time store
scoping does not rewrite the migration model.

## Enqueue usage

```csharp
var jobId = await jobs.EnqueueAsync(
    handlerName: "SendEmail",                       // matches an AddHandler registration
    jobName: $"send-email-order-{orderId}",         // unique key in the external scheduler
    payload: new SendEmailJobArgs { To = "x@y.z" }, // wrapped in a CloudEventEnvelope carrying the schema
    schedule: "@daily",                             // cron / @-period ⇒ Recurring; ISO-8601 instant ⇒ OneShot
    kind: JobKind.Recurring);                       // optional — omit to infer the kind from the schedule
```

By default (`directly: false`), `EnqueueAsync` writes a single `Pending` row (with the serialized envelope as
its payload), returns the job id, and makes **no scheduler call**; the arming poller registers the schedule
after the row commits. With `directly: true`, the service attempts to register the schedule only after
persistence commits (through the ambient UoW's `OnCompleted` callback or after its own UoW commit). This direct
registration attempt does not provide the durable poller-first guarantee described below. The `JobKind` is
inferred from the schedule when `kind` is omitted: a value starting with `@` (e.g. `@every 5m`, `@daily`) or a
5–6 field cron expression ⇒ `Recurring`; anything else (e.g. an ISO-8601 instant) ⇒ `OneShot`.

### Atomic enqueue with the caller's transaction

Enqueue is atomic with the caller's business writes when both DbContexts participate in the same **shared,
transactional** ambient UnitOfWork. The job row is persisted into that UoW, so commit applies both writes and
rollback discards both. When there is no ambient UoW, enqueue opens and commits a short `RequiresNew`
transaction for the job row; there is then no caller transaction with which to provide cross-context
atomicity.

```csharp
await using var uow = uowManager.Begin(
    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

// ... write business data ...

await jobs.EnqueueAsync(
    "SendEmail", jobName, payload, "@once",
    jobId: correlationId);          // optional caller-supplied id to reuse for the caller's own tracking row

await uow.CommitAsync();             // job row commits atomically with the business data
```

The row commits with the business data in one shared transaction. If the caller rolls back, the row never
exists, so there is never an orphaned scheduled job. This atomic boundary ends with enqueue persistence:
poller or inline scheduler arming attempts, handler work, and outcome recording run later in separate
boundaries. No scheduler call or handler execution runs inside the caller's enqueue transaction (which also
avoids nested-UoW / shared-DbContext collisions).

To enqueue a job that must survive a caller rollback, enqueue it outside that transaction (manage the UoW
boundary yourself).

### Arming immediately (`directly`)

By default arming is left entirely to the poller. Pass `directly: true` to attempt to arm the scheduler
**inline** after the persistence transaction commits instead of waiting for the next poller pass. To prevent a
poller race, this path initially persists the job row as `Scheduled`; it does not persist `Pending` and later
flip it to `Scheduled`.

```csharp
await jobs.EnqueueAsync("SendEmail", jobName, payload, "@daily", directly: true);
```

- In the **ambient** case, arming is deferred to the ambient UoW's `OnCompleted` callback, so it only fires
  after the caller's commit (a rollback still discards everything — nothing is armed).
- Without an ambient UoW, the scheduler call runs only after enqueue's own `RequiresNew` UoW commits.
- If the scheduler call throws, the service makes a **best-effort** `Scheduled → Pending` recovery so the poller
  can retry. A process crash, cancellation, or recovery failure can leave the row `Scheduled` without a
  scheduler entry; the poller does not claim `Scheduled` rows. Prefer the default `directly: false` path when
  durable poller-first registration is required.

## Job management

```csharp
// Reschedule: hands the row back to the poller (sets the new schedule, marks it Pending + due now,
// re-infers the kind). Does NOT call the scheduler and does NOT touch the stored payload.
await jobs.UpdateAsync(jobId, "@weekly");

// Cancel: deletes from the scheduler and marks the row Cancelled.
await jobs.DeleteAsync(jobId);
```

## Defining handlers

```csharp
public class SendEmailJobArgs
{
    public required string To { get; set; }
    public required string Subject { get; set; }
}

public class SendEmailJobHandler(IEmailService email) : IBackgroundJobHandler<SendEmailJobArgs>
{
    public async Task HandleAsync(SendEmailJobArgs args, CancellationToken cancellationToken)
        => await email.SendAsync(args.To, args.Subject, cancellationToken);
}
```

Make handlers **idempotent**: the at-least-once delivery guarantee plus the framework retry path means a
handler may run more than once for the same logical job.

### Handler unit-of-work contract (BREAKING)

**The dispatcher no longer wraps the handler in a transaction.** It claims the job (`Scheduled→Running`),
sets up the **schema scope** for the handler, invokes it, and records the outcome in its own short
UnitOfWork — but it holds **no open connection or transaction across your handler**. A handler that touches
the database must therefore **open its own UnitOfWork** around that work:

```csharp
public class GenerateReportJobHandler(
    IUnitOfWorkManager uowManager,
    IReportRepository reports) : IBackgroundJobHandler<GenerateReportArgs>
{
    public async Task HandleAsync(GenerateReportArgs args, CancellationToken cancellationToken)
    {
        // long-running, no DB connection pinned here ...

        await using var uow = uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

        await reports.InsertAsync(/* ... */, cancellationToken);

        await uow.CommitAsync(cancellationToken);   // commit your own boundary
    }
}
```

The schema scope is already active when `HandleAsync` runs, so the UoW you open resolves the right schema's
DbContext automatically — you only own the transaction boundary, not the scope. A handler decorated with
`[UnitOfWork]` works the same way (the aspect opens the boundary for you).

**Why:** background-job handlers can run for a long time. If the dispatcher held a transaction open across the
whole handler, it would pin a database connection for the entire run — exactly what trips PgBouncer's
`idle_in_transaction` limits and exhausts the pool. Owning a short UoW around just the DB writes keeps
connections free while the handler does its slow work, and lets the handler commit/rollback at its own
boundaries.

## Reaper / visibility timeout

A handler can crash, hang, or have its process killed **after** the job was claimed (`Running`) but **before**
an outcome is recorded — leaving the row stuck in `Running` forever. The arming processor includes a **reaper**
phase that recovers these.

The dispatcher stamps `BackgroundJobInfo.RunningSince` when it claims a job. On each pass the arming processor
treats any job whose `RunningSince` is older than `BackgroundJobOptions.VisibilityTimeout` (default **5
minutes**) as a crashed/timed-out execution and resets it (clearing `RunningSince`):

- **One-shot, retries left** → `Retrying` (and re-armed at `NextRetryAt`).
- **Recurring** → `Scheduled` (re-armed).
- **One-shot, retries exhausted** → `Failed`.

```csharp
services.AddAetherBackgroundJob<MyDbContext>(o =>
{
    o.VisibilityTimeout = TimeSpan.FromMinutes(30); // set comfortably ABOVE your longest handler
    // ...
});
```

**Set `VisibilityTimeout` above your longest expected handler duration.** A handler that legitimately runs
longer than the timeout will be reaped while it is still working, causing a duplicate run — which idempotent
handlers tolerate, but it wastes work. The default of 5 minutes suits short handlers; raise it for long ones.

### Visibility timeout & claim tokens

The reaper detects a stale execution purely by elapsed time, so a handler that runs longer than
`VisibilityTimeout` **will** be reaped and re-run while the original is still working. Without a guard, that
slow original could later record its outcome (e.g. `Completed`) by id and silently stomp the reaper's reset
state — double-counting retries or resurrecting a job the reaper already retired.

To prevent this, the atomic claim stamps a fresh **`RunningToken`** (a `Guid`) alongside `RunningSince`. Every
transition **out of** `Running` — by the dispatcher recording an outcome, *and* by the reaper resetting a stuck
job — is a conditional update guarded on `Status == Running && RunningToken == token`:

- The dispatcher carries the token it stamped at claim time.
- The reaper carries the token it observed on the stale row.

Whichever actor commits first wins the row update; the other affects **0 rows** and skips (the dispatcher logs
`claim-lost` and does not delete from the scheduler or touch state). As a result a retry's `RetryCount`
increments **exactly once per claim**, and a slow original execution can never overwrite the reaper's/retry's
state.

`VisibilityTimeout` is therefore a **hard ceiling** on handler runtime, not a soft hint: size it comfortably
above your slowest handler so legitimate long runs are not reaped mid-flight.

> **Migration.** This adds a nullable `RunningToken` column (`uuid` on PostgreSQL, `uniqueidentifier` on SQL
> Server) to the `BackgroundJobs` table. Consumers must add an EF Core migration (e.g.
> `dotnet ef migrations add AddBackgroundJobRunningToken`) and apply it before deploying. The column is nullable
> with no default, so existing rows are unaffected.
>
> **Upgrade ordering.** A job left in `Running` by the previous build has a null `RunningToken`, and the
> token-guarded reaper will not reset it (its guard matches no row). Let in-flight `Running` jobs drain (or
> manually reset any stuck `Running` rows to `Scheduled`/`Pending`) when upgrading, so none are left
> permanently un-reapable.

## Concepts

- **Job table as source of truth + arming poller.** The row is durably persisted before anything is scheduled.
  The default path persists `Pending` and leaves registration to the poller. `directly: true` instead persists
  `Scheduled`, then attempts registration after commit; its recovery to `Pending` on scheduler failure is
  best-effort and is not crash-safe. Use the default path for the durable poller-first guarantee.

- **Optimistic-concurrency CAS claim (at-least-once safe).** The dispatcher claims a job with a conditional
  `Scheduled→Running` update (`IJobStore.TryTransitionStatusAsync`). If two deliveries race, exactly one wins
  the row update and runs the handler; the loser observes the row is no longer `Scheduled` and skips. Combined
  with idempotent handlers this makes duplicate Dapr deliveries safe.

- **Framework-managed retry + exponential backoff (one-shot only).** On one-shot failure with retries left,
  the job goes `Retrying` with `NextRetryAt = now + RetryBaseDelay * 2^retryCount` (capped at one hour). The
  poller re-arms it as a one-shot at `NextRetryAt`. When retries are exhausted the job is `Failed` and removed
  from the scheduler.

- **Recurring retry semantics.** Recurring jobs do **not** use framework backoff. A failed run records the
  error and returns to `Scheduled`; the job simply runs again on its next cron occurrence.

- **One-shot vs recurring deletion.** A finished one-shot (`Completed` or `Failed`) is deleted from the
  scheduler; a recurring job stays armed so it keeps firing.

## Tracing

All background-job operations automatically emit OpenTelemetry spans via the
`BBT.Aether.Infrastructure` ActivitySource. No additional configuration is needed when using
`AddAetherTelemetry`.

### Spans

| Span | Description |
|------|-------------|
| `BackgroundJob.Enqueue` | Job creation + persistence via `IBackgroundJobService` |
| `BackgroundJob.Update` | Schedule update via `IBackgroundJobService` |
| `BackgroundJob.Delete` | Job cancellation via `IBackgroundJobService` |
| `BackgroundJob.Schedule` | Scheduler-level job registration (e.g. Dapr) |
| `BackgroundJob.Schedule.Update` | Scheduler-level reschedule |
| `BackgroundJob.Schedule.Delete` | Scheduler-level job removal |
| `BackgroundJob.Execute` | Dapr callback entry point (execution bridge) |
| `BackgroundJob.Dispatch` | Handler invocation |

### Tags

| Tag | Description |
|-----|-------------|
| `job.handler_name` | Handler type name (e.g. `"SendEmail"`) |
| `job.name` | Unique job identifier (e.g. `"send-email-order-123"`) |
| `job.schedule` | Schedule expression (e.g. `"@daily"`, `"*/15 * * * *"`) |
| `job.id` | Entity ID from `BackgroundJobInfo` |
| `job.kind` | `OneShot` or `Recurring` |
| `job.scheduler` | Scheduler backend (e.g. `"dapr"`) |
| `job.status` | Final dispatch status (`"completed"`, `"failed"`, `"cancelled"`) |

## Known follow-up

- `failurePolicyOptions` is accepted by `EnqueueAsync` (and `IJobScheduler.ScheduleAsync` /
  `ScheduleOneShotAsync`) but is **not yet threaded through the arming poller** — the poller currently arms
  with the scheduler's default failure policy. The parameter is kept on the signature so callers don't break
  and a later task can persist/honor it (see the `TODO(jobs)` in `BackgroundJobService.EnqueueAsync`). The
  Dapr-level failure policy is a secondary delivery-retry safety net and currently applies only to direct
  scheduling, not to poller-armed jobs.

## Related features

- [Unit of Work](../unit-of-work/README.md) — the transaction model the enqueue/dispatch paths build on.
- [Multi-Schema](../multi-schema/ADOPTION-GUIDE.md) — tenant handler scoping and fixed infrastructure persistence.
- [Distributed Lock](../distributed-lock/README.md) — coordinate work across instances inside a handler.
```
