# Middleware-Owned UnitOfWork + Remove Prepare — Design

> Brainstormed + approved (2026-06-23). Replaces the Prepare/reserve activation pattern with an
> eagerly-begun, middleware-owned request UnitOfWork; the `[UnitOfWork]` aspect joins it (Required)
> or owns its own (RequiresNew / no ambient). The Prepare machinery is removed entirely.

## Context & problem

Today the request UoW lifecycle is a two-step *reserve → activate* pattern:

- `UnitOfWorkMiddleware` calls `_uowManager.Prepare("AetherUow")` — creates a **placeholder** scope that is
  NOT initialized. `IAmbientUnitOfWorkAccessor.GetActiveUnitOfWork()` skips `IsPrepared` scopes, so
  `uowManager.Current` is **null** until something activates it.
- The `[UnitOfWork]` aspect activates it via `TryBeginPreparedAsync("AetherUow", options)`.

Two problems fall out of this:

1. **`No active UnitOfWork.`** — If an application-service method calls a repository directly but is **not**
   intercepted by the `[UnitOfWork]` aspect (aspect not woven, attribute not applied, or the call is from a
   controller/minimal-API/background path), the prepared placeholder is never activated and
   `AetherDbContextProvider` throws `No active UnitOfWork.`. "Direct repo usage" only works when an *active*
   UoW exists in the ambient flow — and the middleware alone never produces one.
2. **No commit in the prepared path.** Both the middleware's `scope.CommitAsync(...)` and the aspect's
   prepared-branch `Current.CommitAsync(...)` are commented out. Each defers to the other, so an activated
   request UoW is disposed without commit → `DisposeAsync` rolls it back. Reads are unaffected; **writes via
   this path silently do not persist.**

The reserve pattern's historical justification — "don't open a DB connection for requests that never touch
the DB" — **does not apply here**: in `CompositeUnitOfWork`, `Begin`/`InitializeCore` does **no I/O**; the
connection and transaction open lazily on the first `GetDbContextAsync`. So eagerly beginning a UoW costs
only an object allocation + an AsyncLocal write.

## Decision

Make the middleware **own** an eagerly-begun request UoW, make the aspect **join or own** per its `Scope`,
and **remove the Prepare machinery** entirely. One rule governs commit: **only the owner commits/rolls back.**

### Behavior model

**`UnitOfWorkMiddleware`** (for non-excluded requests) — owner of the request UoW:

```csharp
await using var uow = _uowManager.Begin(_options.DefaultOptions); // Required; no I/O, lazy connection
try
{
    await next(context);
    await uow.CommitAsync(context.RequestAborted);
}
catch
{
    await uow.RollbackAsync(context.RequestAborted);
    throw;
}
```

- Eager `Begin` (not `Prepare`). `Current` is immediately an **active** UoW, so a direct repository call in any
  downstream service works without the aspect — fixing problem #1.
- Real commit on success / rollback on exception — fixing problem #2.
- Exclusion logic (`ShouldStartUnitOfWork`, `ExcludedMethods`, `ExcludedPathPrefixes`, `ExcludeWhen`,
  WebSocket skip) is unchanged. Excluded paths still get no UoW (correct — health/swagger/dapr must not run
  domain repos).

**`[UnitOfWork]` aspect** — honors `Scope`; only owns when it creates the root:

- `Scope == RequiresNew` → `Begin(RequiresNew)` (independent root, own connection/transaction), proceed,
  commit on success / rollback on exception. Independent of the middleware UoW. `IsTransactional` /
  `IsolationLevel` apply here.
- `Scope == Suppress` → `Begin(Suppress)`, proceed (no commit/rollback).
- `Scope == Required` (default):
  - **`uowManager.Current` is active** (middleware already began, or an outer aspect owns) → **participate**:
    just `ProceedAsync()`. Do **not** commit/roll back the root; exceptions propagate to the owner, which
    rolls back. No per-method `SaveChangesAsync` — the owner's `CommitAsync` flushes every materialized
    context for the whole request. (Deliberate simplification vs. the old prepared branch, which flushed
    per-method; if a method needs a mid-flow flush to read back DB-generated values it can flush explicitly
    via its repository/UoW.)
  - **no active UoW** (non-HTTP path, no middleware) → **own**: `Begin(Required)` creates the root, proceed,
    commit on success / rollback on exception. This preserves today's fallback and satisfies "if there is no
    middleware-started UoW, the aspect starts one."

Ownership is decided in the aspect by inspecting `uowManager.Current` **before** `Begin` (for `Required`).
This keeps the change local to the aspect and avoids altering core scope-commit semantics.

### Commit / rollback responsibility (single rule)

| Actor | When it owns | Commit / rollback |
|---|---|---|
| Middleware | always (HTTP request) | commits on success, rolls back on exception |
| Aspect `RequiresNew` | always | commits on success, rolls back on exception (independent) |
| Aspect `Required`, no ambient | yes | commits on success, rolls back on exception |
| Aspect `Required`, ambient exists | no (participant) | nothing — owner handles it; exceptions propagate |

`RollbackAsync` and `DisposeAsync` are already idempotent/guarded (this session's earlier fixes), so a
participant's exception propagating to the owner's rollback, followed by dispose, is safe and fires failed
handlers at most once.

### Remove the Prepare machinery (full removal — approved)

Delete (no test references exist; only the framework internals below use these):

- `IUnitOfWorkManager`: `Prepare(string, bool)`, `TryBeginPreparedAsync(...)`.
- `IUnitOfWork`: `Prepare(string)`, `IsPrepared`, `IsPreparedFor(string)`, `PreparationName`. Also the sync
  `Initialize(UnitOfWorkOptions)` **iff** it becomes orphaned after prepare removal (it was the activation
  hook). Keep `InitializeAsync` / `InitializeCore` — `Begin`/`BeginAsync` use them.
- `UnitOfWorkScope`: `_isPrepared`, `_preparationName`, `_options`, `Prepare`, `Initialize`, `IsPrepared`,
  `IsPreparedFor`, `PreparationName`, and every `if (_isPrepared)` guard in `GetDbContextAsync` /
  `SaveChangesAsync` / `CommitAsync` / `RollbackAsync`. `Options => _root.Options` (the T5 fallback becomes
  the only source, since the scope no longer carries its own options).
- `CompositeUnitOfWork`: `Prepare` (throws), `IsPrepared => false`, `PreparationName => null`,
  `IsPreparedFor => false`. Keep the `Initialize` throw only if `IUnitOfWork.Initialize` is kept; otherwise
  remove both.
- `SuppressedUowScope`: `Prepare`, `IsPrepared`, `IsPreparedFor`, `PreparationName` (drop with the interface).
- `AsyncLocalAmbientUowAccessor.GetActiveUnitOfWork`: drop `uow.IsPrepared` from the skip condition; keep
  `IsCompleted` / `IsDisposed`.
- `UnitOfWorkOptions.PrepareName` constant.
- `UnitOfWorkMiddleware`: rewrite to the eager-begin owner shape above.
- `UnitOfWorkAttribute`: rewrite `OnInvokeAsync` to the join-or-own shape above; remove the
  `TryBeginPreparedAsync` branch. `OnInvoke` (sync bridge) unchanged.

## Accepted trade-offs

- **Request = one transaction (Required).** All DB work in a request shares one connection + transaction and
  is atomic per request. Tighter boundaries use `[UnitOfWork(RequiresNew)]` or an explicit `Begin`.
- **Transaction held for request duration.** The transaction opens on the first repo call and stays open
  until the middleware disposes at request end; a slow external call *after* the first DB hit holds the
  pooled server connection (PgBouncer) for that span. This equals today's behavior for an activated request
  UoW — not a regression — but now applies to every DB-touching request. Scope tighter with `RequiresNew`
  where it matters.
- **GET and all non-excluded methods get a UoW.** This is what makes a direct read query work (the reported
  bug). A read-only request commits an empty/read transaction (effectively a no-op if nothing materialized).
- **Aspect `IsTransactional` / `IsolationLevel` are ignored when participating** (the owner's options win);
  they apply only when the aspect owns (`RequiresNew`, or `Required` with no ambient). Note `IsTransactional`
  is already effectively moot in `CompositeUnitOfWork` (connection + transaction always open together
  lazily).
- **Middleware ordering.** The UoW middleware must wrap the endpoint and run after schema resolution so the
  ambient schema is set before any repository call. `Begin` itself is schema-independent; only
  `GetDbContextAsync` needs the schema. Keep the existing registration order.

## Verification

- `dotnet build framework/BBT.Aether.slnx -c Release` → 0 errors; `grep -rn "Prepare\|TryBeginPrepared\|IsPrepared\|PreparationName\|PrepareName\|IsPreparedFor" framework/src` → empty.
- New/updated tests:
  - **Direct repo read without the aspect works** under the middleware (the reported bug): a request that
    hits a repository directly (no `[UnitOfWork]` on the method) returns data and does not throw
    `No active UnitOfWork.`.
  - **Middleware commits on success / rolls back on exception** — a write persists on 2xx; the same write is
    discarded when the handler throws.
  - **Aspect `Required` joins the middleware UoW** — work done in an aspected method participates in the same
    transaction (commit/rollback is atomic with the rest of the request); the aspect does not commit early.
  - **Aspect `RequiresNew` is isolated** — its transaction commits independently of an outer/rolled-back
    request UoW.
  - **Aspect owns when there is no ambient UoW** (non-HTTP path) — begins, commits, rolls back on exception.
  - Existing UoW / multi-schema / disposal / rollback tests stay green (no prepared-flow tests exist to
    update).
- Manual: the originally-failing AppService read path returns rows.

## Out of scope

- Changing core `UnitOfWorkScope.CommitAsync`/`RollbackAsync` to make participant commits no-ops at the scope
  level (the more principled "owner-only-commits" enforcement). The aspect-side `Current`-check achieves the
  same outcome with far less blast radius; the scope-level change is a possible future cleanup.
- Outbox/Inbox and background-job paths — they already manage their own `Begin(RequiresNew)` UoWs and are
  unaffected by the middleware/aspect change.
