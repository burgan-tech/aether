# Background Jobs

## Overview

The background-job system runs scheduled and recurring work with the **job table as the single source of
truth**. Enqueue writes a database row only; a per-schema **arming poller** later registers the job in an
external scheduler (default: Dapr Jobs) and the scheduler's fire is routed back through an execution bridge to
a type-safe handler. This row-first design makes enqueue atomic with the caller's business transaction (no
orphaned schedules on rollback), provides framework-managed retry with exponential backoff for one-shot jobs,
and uses an optimistic-concurrency claim so delivery is at-least-once safe.

The pieces:

| Component | Role |
|-----------|------|
| `IBackgroundJobService` (`BackgroundJobService`) | `EnqueueAsync` / `UpdateAsync` / `DeleteAsync`. Writes/updates the job row. Never calls the scheduler on the enqueue path. |
| `BackgroundJobArmingProcessor` | Per-schema poller. Leases due rows, arms them in the scheduler **outside** any transaction, then flips them to `Scheduled`. |
| `IJobScheduler` (`DaprJobScheduler`) | The external scheduler abstraction (`ScheduleAsync`, `ScheduleOneShotAsync`, `DeleteAsync`). |
| `IJobExecutionBridge` (`DaprJobExecutionBridge`) | Dapr trigger entry point: looks the job up (in its own UoW) and delegates to the dispatcher. |
| `IJobDispatcher` (`JobDispatcher`) | CAS-claims `Scheduled→Running`, invokes the handler, records the outcome. |
| `IJobStore` (`EfCoreJobStore<TDbContext>`) | EF Core persistence + the atomic status transitions. |

## Lifecycle

```
                 EnqueueAsync (writes row, no scheduler call)
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
                         ▼  (CAS Scheduled→Running)                │
                     ┌─────────┐                                   │
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
    o.Schema = "orders";                          // the schema this poller instance arms (see below)
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

### Per-schema arming

The `Schema` option binds **one poller instance to one schema**. Each `RunAsync` opens a UnitOfWork scoped to
`Schema`, so the poller only ever arms that schema's rows. If `Schema` is null/empty the poller logs a warning
and does nothing. For a multi-schema deployment, register one configured poller per schema.

## Enqueue usage

```csharp
var jobId = await jobs.EnqueueAsync(
    handlerName: "SendEmail",                       // matches an AddHandler registration
    jobName: $"send-email-order-{orderId}",         // unique key in the external scheduler
    payload: new SendEmailJobArgs { To = "x@y.z" }, // wrapped in a CloudEventEnvelope carrying the schema
    schedule: "@daily",                             // cron / @-period ⇒ Recurring; ISO-8601 instant ⇒ OneShot
    kind: JobKind.Recurring);                       // optional — omit to infer the kind from the schedule
```

`EnqueueAsync` writes a single `Pending` row (with the serialized envelope as its payload) and returns the
job id. It makes **no scheduler call** — the arming poller arms the row after it commits. The `JobKind` is
inferred from the schedule when `kind` is omitted: a value starting with `@` (e.g. `@every 5m`, `@daily`) or a
5–6 field cron expression ⇒ `Recurring`; anything else (e.g. an ISO-8601 instant) ⇒ `OneShot`.

### Atomic enqueue with the caller's transaction

Pass `useAmbientUnitOfWork: true` to persist the job row into the **caller's** ambient UnitOfWork instead of a
private one:

```csharp
await using var uow = uowManager.Begin(/* transactional */);

// ... write business data ...

await jobs.EnqueueAsync(
    "SendEmail", jobName, payload, "@once",
    useAmbientUnitOfWork: true,
    jobId: correlationId);   // optional caller-supplied id to reuse for the caller's own tracking row

await uow.CommitAsync();      // job row commits atomically with the business data
```

The row commits with the business data in one transaction. If the caller rolls back, the row never exists, so
there is never an orphaned scheduled job. The poller arms the row only after a successful commit, and the
scheduler is only ever touched after that — no scheduler call runs inside the caller's transaction (which also
avoids nested-UoW / shared-DbContext collisions).

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

## Concepts

- **Job table as source of truth + arming poller.** The row exists before anything is scheduled, and the
  poller is the only thing that registers schedules. A crash between commit and arming leaves a `Pending` row
  the next poller pass picks up — there are no orphaned scheduler entries and no lost jobs.

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
- [Multi-Schema](../multi-schema/ADOPTION-GUIDE.md) — schema scoping, which the per-schema poller relies on.
- [Distributed Lock](../distributed-lock/README.md) — coordinate work across instances inside a handler.
```
