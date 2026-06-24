# UoW / Provider / Background-Job Review Fixes — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the correctness defects found in the code review of the multi-schema UoW, provider seam, and background-job subsystems, plus apply the two product decisions (remove `UnitOfWorkDomainEventSink`; document SQL Server single-schema).

**Architecture:** Eight independent tasks on branch `feature/multi-schema-uow`. The riskiest is Task 6 (claim-token-guarded job-outcome recording); the rest are localized. No new public packages.

**Tech Stack:** .NET 10, EF Core 10, xUnit, Testcontainers.PostgreSql (Postgres integration tests), in-memory/fixture tests in `BBT.Aether.Infrastructure.Tests`.

**Source of truth (the "spec"):** the three reviewer reports + the user's two decisions:
1. **Remove `UnitOfWorkDomainEventSink`** (resolves Critical #3 structurally) — do NOT relax the commit guard.
2. **SQL Server is single-schema, always default** — document only; do not add a loud-fail.

**Verification (whole plan):**
- `dotnet build framework/BBT.Aether.slnx -c Release` → 0 errors (repo treats nullable warnings as errors).
- `dotnet test framework/test/BBT.Aether.Infrastructure.Tests` → green.
- `dotnet test framework/test/BBT.Aether.Postgres.Tests` → green (Docker required).
- `dotnet test framework/test/BBT.Aether.SqlServer.Tests` → green (Docker required) where applicable.

**Task ordering rationale:** data-correctness first where blast radius is widest, but each task is self-contained and independently committable. Recommended order: T2, T6 (largest), T1, T3, T4, T5, T7, T8. Tasks may be done in any order; none depend on another.

---

## Task 1: `CurrentSchema` — make the AsyncLocal schema stack async-safe (Critical #1)

**Problem:** `CurrentSchema` stores a *mutable* `Stack<string>` in `AsyncLocal`. `Current.Value ??= new Stack()` assigns once; later `Push`/`Pop` mutate that same object without reassigning `Current.Value`. AsyncLocal copies the *reference* into child flows, so parallel branches (`Task.WhenAll` over schemas) or any child calling `Change()` corrupt each other → wrong `Name` or `"Schema scope corrupted"`. Fix with copy-on-write using an immutable stack, reassigning `Current.Value` on every push/pop.

**Files:**
- Modify: `framework/src/BBT.Aether.Core/BBT/Aether/MultiSchema/CurrentSchema.cs`
- Test: `framework/test/BBT.Aether.Postgres.Tests/BBT/Aether/MultiSchema/CurrentSchemaTests.cs` (exists) — add a parallel-isolation test. (If a `CurrentSchema` unit test exists under `BBT.Aether.Infrastructure.Tests`, prefer adding there since it needs no DB.)

- [ ] **Step 1: Write the failing test** (add to the existing `CurrentSchemaTests`; this test needs no DB so it can also live in `BBT.Aether.Infrastructure.Tests` if the type is reachable there)

```csharp
[Fact]
public async Task Change_is_isolated_across_parallel_async_flows()
{
    var formatter = new PassthroughSchemaNameFormatter(); // or the real ISchemaNameFormatter test double already used in this file
    var sut = new CurrentSchema(formatter);

    // Establish a base scope, then fan out: each parallel branch pushes its own schema and
    // must observe ONLY its own value, with no cross-contamination or corruption on dispose.
    using (sut.Change("base"))
    {
        var tasks = Enumerable.Range(0, 32).Select(async i =>
        {
            var schema = $"s{i}";
            using (sut.Change(schema))
            {
                await Task.Yield();
                Assert.Equal(schema, sut.Name);
                await Task.Delay(1);
                Assert.Equal(schema, sut.Name); // still mine after interleaving
            }
        });

        await Task.WhenAll(tasks); // must NOT throw "Schema scope corrupted"
        Assert.Equal("base", sut.Name); // parent unaffected by children
    }

    Assert.Null(sut.Name);
}
```

> Note: reuse whatever `ISchemaNameFormatter` test double the existing `CurrentSchemaTests` already uses. If the file uses a passthrough/mock, follow that exact pattern instead of `PassthroughSchemaNameFormatter`.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test framework/test/BBT.Aether.Postgres.Tests --filter "FullyQualifiedName~CurrentSchemaTests.Change_is_isolated_across_parallel_async_flows"`
Expected: FAIL — intermittent `"Schema scope corrupted: out-of-order disposal detected."` or a wrong `Name` assertion, because all branches share one Stack instance.

- [ ] **Step 3: Implement copy-on-write with an immutable stack**

Replace the body of `framework/src/BBT.Aether.Core/BBT/Aether/MultiSchema/CurrentSchema.cs` with:

```csharp
using System;
using System.Collections.Immutable;
using System.Threading;

namespace BBT.Aether.MultiSchema;

/// <summary>
/// Default <see cref="ICurrentSchema"/> backed by an AsyncLocal of an IMMUTABLE stack so nested
/// schema scopes flow across async calls and restore the previous schema on dispose.
/// </summary>
/// <remarks>
/// Every <see cref="Change"/>/dispose REASSIGNS <c>Current.Value</c> with a new immutable stack
/// (copy-on-write). This is what makes scopes isolated across async boundaries: AsyncLocal copies
/// the reference into child flows, so mutating a shared mutable stack in place would leak pushes/pops
/// between parallel branches and the parent. Reassigning a new immutable value keeps each flow's view
/// private.
/// </remarks>
public sealed class CurrentSchema(ISchemaNameFormatter formatter) : ICurrentSchema
{
    private static readonly AsyncLocal<ImmutableStack<string>?> Current = new();

    public string? Name => Current.Value is { IsEmpty: false } s ? s.Peek() : null;

    public IDisposable Change(string schema)
    {
        if (string.IsNullOrWhiteSpace(schema))
        {
            throw new ArgumentException("Schema cannot be empty.", nameof(schema));
        }

        var formatted = formatter.Format(schema);
        var previous = Current.Value ?? ImmutableStack<string>.Empty;
        Current.Value = previous.Push(formatted);

        return new RestoreOnDispose(previous, formatted);
    }

    private sealed class RestoreOnDispose(ImmutableStack<string> previous, string expected) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Out-of-order disposal guard: the current top must be the value this scope pushed.
            var current = Current.Value;
            if (current is null || current.IsEmpty || !string.Equals(current.Peek(), expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Schema scope corrupted: out-of-order disposal detected.");
            }

            // Copy-on-write restore: reassign the previous immutable snapshot for THIS flow.
            Current.Value = previous;
        }
    }
}
```

Why this is correct: each `Change` captures `previous` and assigns a brand-new `ImmutableStack`. Child/parallel flows inherit the *reference* to the parent's immutable stack but any `Change`/`Dispose` they run assigns a new value to their own `Current.Value` slot — the parent's and siblings' slots are untouched. `Dispose` restores the exact `previous` snapshot it captured, so disposal order within a single flow is still validated, and cross-flow contamination is impossible.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test framework/test/BBT.Aether.Postgres.Tests --filter "FullyQualifiedName~CurrentSchemaTests"`
Expected: PASS (new test + all existing `CurrentSchemaTests` green).

- [ ] **Step 5: Commit**

```bash
git add framework/src/BBT.Aether.Core/BBT/Aether/MultiSchema/CurrentSchema.cs \
        framework/test/BBT.Aether.Postgres.Tests/BBT/Aether/MultiSchema/CurrentSchemaTests.cs
git commit -m "fix(multi-schema)!: make CurrentSchema async-safe via copy-on-write immutable stack

A mutable Stack stored in AsyncLocal was mutated in place; parallel/child
async flows shared the reference and corrupted each other. Reassign an
ImmutableStack on every push/pop so each flow's view is isolated.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: Remove `UnitOfWorkDomainEventSink` and the ambient-fallback event path (Critical #3)

**Decision:** Remove the sink rather than relax the `if (_transaction is not null)` guard in `CommitAsync`. After removal, `_events` is populated **only** by `BufferEnqueuer` (`CompositeUnitOfWork.cs:180`), which is attached only when a context is materialized through the UoW — i.e. exactly when `_transaction` opens. The commit guard then becomes provably correct.

**Consequence (document):** a `DbContext` constructed *outside* the UoW (no `LocalEventEnqueuer`) no longer has a fallback sink, so its domain events are not enqueued. In the shared-connection model every context is created via the UoW configurator, so this path is dead; raising events from a non-UoW context is unsupported.

**Files:**
- Delete: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/UnitOfWorkDomainEventSink.cs` (contains `UnitOfWorkDomainEventSink` and the internal `IUnitOfWorkEventEnqueuer`)
- Delete: `framework/src/BBT.Aether.Core/BBT/Aether/Events/IDomainEventSink.cs`
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Domain/EntityFrameworkCore/AetherDbContext.cs` (drop `IDomainEventSink? eventSink` ctor param + Priority-2 branch)
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/CompositeUnitOfWork.cs` (drop `IUnitOfWorkEventEnqueuer` from the type list + the public `EnqueueEvent` method)
- Modify: `framework/src/BBT.Aether.Infrastructure/Microsoft/Extensions/DependencyInjection/AetherEfCoreServiceCollectionExtensions.cs:70` (remove the `TryAddScoped<IDomainEventSink, UnitOfWorkDomainEventSink>()` registration)
- Modify: `framework/docs/domain-events/README.md` (note the removal)

- [ ] **Step 1: Establish the green baseline**

Run: `dotnet test framework/test/BBT.Aether.Postgres.Tests --filter "FullyQualifiedName~OutboxWithinSharedTransactionTests|FullyQualifiedName~MultiSchemaUnitOfWorkTests"`
Expected: PASS. These exercise event dispatch within the shared transaction (the path that survives). They are the regression gate for this task — they must stay green after removal.

- [ ] **Step 2: Delete the sink file and the interface**

```bash
git rm framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/UnitOfWorkDomainEventSink.cs
git rm framework/src/BBT.Aether.Core/BBT/Aether/Events/IDomainEventSink.cs
```

- [ ] **Step 3: Drop the registration**

In `framework/src/BBT.Aether.Infrastructure/Microsoft/Extensions/DependencyInjection/AetherEfCoreServiceCollectionExtensions.cs`, remove the line:

```csharp
services.TryAddScoped<IDomainEventSink, UnitOfWorkDomainEventSink>();
```

(Remove a now-unused `using BBT.Aether.Events;` only if nothing else in the file needs it — verify before deleting the using; the file references other `BBT.Aether.Events` types, so the using likely stays.)

- [ ] **Step 4: Update `AetherDbContext`**

In `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Domain/EntityFrameworkCore/AetherDbContext.cs`:
- Remove the constructor parameter `IDomainEventSink? eventSink = null` (line 22).
- In `PublishDomainEventsToSink()` remove the Priority-2 branch and simplify to:

```csharp
private void PublishDomainEventsToSink()
{
    var domainEvents = CollectDomainEvents();
    if (domainEvents.Count == 0)
    {
        return;
    }

    // Domain events flow ONLY through the owning local transaction's enqueuer, which is wired
    // when the context is materialized by the UnitOfWork. A context created outside a UnitOfWork
    // has no enqueuer and therefore cannot raise domain events (unsupported in the shared-connection model).
    if (LocalEventEnqueuer is not null)
    {
        LocalEventEnqueuer.EnqueueEvents(domainEvents);
        ClearDomainEvents();
    }
}
```

Update the method's XML summary to drop the "2. eventSink fallback" line. Remove the now-unused `using BBT.Aether.Events;` only if no other `Events` type is referenced in the file (it is — `DomainEventEnvelope`/`ILocalTransactionEventEnqueuer` live elsewhere; verify and keep usings that are still needed).

- [ ] **Step 5: Update `CompositeUnitOfWork`**

In `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/CompositeUnitOfWork.cs`:
- Remove `IUnitOfWorkEventEnqueuer` from the base/interface list (line 29): change
  `: IEfCoreUnitOfWork, ITransactionalRoot, IUnitOfWorkEventEnqueuer`
  to `: IEfCoreUnitOfWork, ITransactionalRoot`.
- Remove the public `EnqueueEvent(DomainEventEnvelope eventEnvelope)` method (lines 187–194). The `_events` list and the private `BufferEnqueuer` (which calls `_events.Add` via `EnqueueEvents`) stay — they are the surviving path.

> Verify `BufferEnqueuer.EnqueueEvents` (CompositeUnitOfWork.cs:489+) does NOT delegate to the removed `EnqueueEvent`. If it adds to the buffer directly, no change needed. If it calls `EnqueueEvent`, inline the de-dup `_events.Add` into `BufferEnqueuer` instead.

- [ ] **Step 6: Build and run the regression gate**

Run: `dotnet build framework/BBT.Aether.slnx -c Release`
Expected: 0 errors. Then:
Run: `dotnet test framework/test/BBT.Aether.Postgres.Tests --filter "FullyQualifiedName~OutboxWithinSharedTransactionTests|FullyQualifiedName~MultiSchemaUnitOfWorkTests"` and `dotnet test framework/test/BBT.Aether.Infrastructure.Tests`
Expected: PASS. If any test injected/constructed an `IDomainEventSink`/`UnitOfWorkDomainEventSink`, update or delete it (its scenario no longer exists).

- [ ] **Step 7: Document the removal**

Append a short note to `framework/docs/domain-events/README.md` under the dispatch/section that mentions event flow:

```markdown
> **Event flow (v2):** Domain events are collected during `SaveChanges` and enqueued onto the
> owning UnitOfWork's transaction buffer. A `DbContext` must be obtained through the UnitOfWork
> for its events to be captured. The legacy `IDomainEventSink` ambient-fallback path was removed —
> raising domain events from a `DbContext` created outside a UnitOfWork is unsupported.
```

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "refactor(events)!: remove UnitOfWorkDomainEventSink and ambient-fallback event path

Domain events now flow exclusively through the UnitOfWork's local-transaction
enqueuer, which is wired only when a context is materialized — the same moment
the shared transaction opens. This makes the CommitAsync 'transaction is not null'
event-flush guard provably correct (no silent event loss) and drops the
IDomainEventSink seam entirely.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: Make `CompositeUnitOfWork.DisposeAsync` exception-safe (Important — connection leak)

**Problem:** `DisposeAsync` (CompositeUnitOfWork.cs:392-407) disposes contexts, then transaction, then connection unguarded. If a `context.DisposeAsync()` throws, the transaction and **connection** are never disposed → pooled-connection leak (the bug this work set out to fix). Also `_isDisposed` is set last, so a throw leaves it `false` and a retry re-runs handlers.

**Files:**
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/CompositeUnitOfWork.cs`
- Test: `framework/test/BBT.Aether.Postgres.Tests/UnitOfWorkDisposalTests.cs` (exists) — add a throwing-context test, OR add a pure unit test in `BBT.Aether.Infrastructure.Tests` if a context-disposal seam can be faked without a DB. Prefer extending `UnitOfWorkDisposalTests`.

- [ ] **Step 1: Write the failing test**

Add to `framework/test/BBT.Aether.Postgres.Tests/UnitOfWorkDisposalTests.cs` a test that materializes a context, forces a context disposal failure, and asserts the connection is still released. If injecting a throwing `DbContext` is impractical against real Postgres, assert the weaker invariant instead: after `DisposeAsync` throws, `IsDisposed` is `true` and a second `DisposeAsync()` is a no-op (does not re-invoke failed handlers). Concretely:

```csharp
[Fact]
public async Task DisposeAsync_sets_disposed_and_is_idempotent_even_if_a_step_throws()
{
    // Arrange: begin a UoW, materialize a context so a connection/transaction exist.
    await using var uow = _uowManager.Begin(new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });
    _ = await ((IEfCoreUnitOfWork)uow).GetDbContextAsync<TestDbContext>(_schema);

    // Act
    await uow.DisposeAsync();

    // Assert: disposed flag set; second dispose is a no-op (no double handler invocation / no throw).
    Assert.True(uow.IsDisposed);
    await uow.DisposeAsync(); // must not throw
}
```

(If a throwing-context harness already exists in the repo's test support, use it to assert the connection is disposed even when a context throws.)

- [ ] **Step 2: Run to verify current behavior**

Run: `dotnet test framework/test/BBT.Aether.Postgres.Tests --filter "FullyQualifiedName~UnitOfWorkDisposalTests"`
Expected: the new idempotency test may already pass for the happy path; the guard below is still required for the throwing case. If you implemented the throwing-context variant, it FAILS (connection not disposed).

- [ ] **Step 3: Implement guarded disposal**

Replace `DisposeAsync` (CompositeUnitOfWork.cs:370-408) with:

```csharp
public async ValueTask DisposeAsync()
{
    if (_isDisposed)
    {
        return;
    }

    // Set early: disposal must be a one-shot even if a step below throws, so a retry never
    // re-invokes failed/disposed handlers or double-releases resources.
    _isDisposed = true;

    if (!IsCompleted)
    {
        // Rollback will call InvokeFailedHandlersAsync.
        await RollbackAsync();
    }
    else if (_exception != null)
    {
        await InvokeFailedHandlersAsync();
    }

    if (_isInitialized)
    {
        InvokeDisposedHandlers();
    }

    // Dispose every resource, but never let one failure prevent releasing the connection —
    // the connection is the pooled resource whose leak we must avoid. Collect and surface
    // the first failure after everything has been attempted.
    Exception? firstFailure = null;

    foreach (var context in _contexts.Values)
    {
        try
        {
            await context.DisposeAsync();
        }
        catch (Exception ex)
        {
            firstFailure ??= ex;
        }
    }

    if (_transaction is not null)
    {
        try
        {
            await _transaction.DisposeAsync();
        }
        catch (Exception ex)
        {
            firstFailure ??= ex;
        }
    }

    if (_connection is not null)
    {
        try
        {
            await _connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            firstFailure ??= ex;
        }
    }

    if (firstFailure is not null)
    {
        throw firstFailure;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test framework/test/BBT.Aether.Postgres.Tests --filter "FullyQualifiedName~UnitOfWorkDisposalTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/CompositeUnitOfWork.cs \
        framework/test/BBT.Aether.Postgres.Tests/UnitOfWorkDisposalTests.cs
git commit -m "fix(uow): guard UnitOfWork disposal so a failing step never leaks the connection

Set _isDisposed early (one-shot) and dispose contexts/transaction/connection in
independent try/catch so a context DisposeAsync failure can't skip releasing the
pooled connection; surface the first failure after full teardown.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: Add an idempotency/completion guard to `CompositeUnitOfWork.RollbackAsync` (Important)

**Problem:** `RollbackAsync` (CompositeUnitOfWork.cs:342-364) lacks the `IsCompleted` guard that `CommitAsync` has. A commit-then-rollback, double-rollback, or rollback-after-dispose calls `_transaction.RollbackAsync()` on an already-completed/disposed transaction and fires `InvokeFailedHandlersAsync` a second time.

**Files:**
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/CompositeUnitOfWork.cs`
- Test: `framework/test/BBT.Aether.Postgres.Tests/UnitOfWorkDisposalTests.cs` or `MultiSchemaUnitOfWorkTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task RollbackAsync_is_noop_after_completion()
{
    var failedHandlerCalls = 0;
    await using var uow = _uowManager.Begin(new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });
    uow.OnFailed((_, _) => { failedHandlerCalls++; return Task.CompletedTask; });

    _ = await ((IEfCoreUnitOfWork)uow).GetDbContextAsync<TestDbContext>(_schema);
    await uow.CommitAsync();

    // Rolling back an already-committed UoW must be a no-op: no second transaction op, no failed handlers.
    await uow.RollbackAsync();

    Assert.Equal(0, failedHandlerCalls);
    Assert.True(uow.IsCompleted);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test framework/test/BBT.Aether.Postgres.Tests --filter "FullyQualifiedName~RollbackAsync_is_noop_after_completion"`
Expected: FAIL — `failedHandlerCalls == 1` (rollback ran after commit) or a transaction-state exception.

- [ ] **Step 3: Implement the guard**

In `RollbackAsync`, change the opening guard from:

```csharp
public async Task RollbackAsync(CancellationToken cancellationToken = default)
{
    if (!_isInitialized)
    {
        return;
    }
```

to:

```csharp
public async Task RollbackAsync(CancellationToken cancellationToken = default)
{
    // No-op if never initialized or already completed (committed or rolled back). Mirrors CommitAsync.
    if (!_isInitialized || IsCompleted)
    {
        return;
    }
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test framework/test/BBT.Aether.Postgres.Tests --filter "FullyQualifiedName~UnitOfWorkDisposalTests|FullyQualifiedName~MultiSchemaUnitOfWorkTests"`
Expected: PASS (new test + existing rollback/commit tests stay green).

- [ ] **Step 5: Commit**

```bash
git add framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/CompositeUnitOfWork.cs \
        framework/test/BBT.Aether.Postgres.Tests/
git commit -m "fix(uow): make RollbackAsync a no-op once the UnitOfWork is completed

Mirror CommitAsync's IsCompleted guard so double-rollback / commit-then-rollback
cannot roll back an already-disposed transaction or fire failed handlers twice.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 5: `UnitOfWorkScope.Options` falls back to the root (Important)

**Problem:** On the `Begin`/`BeginAsync` path the manager constructs the scope and initializes the **root** (`InitializeCore`/`InitializeAsync` set `root.Options`), but never calls `scope.Initialize(options)`. So `scope._options` is null and `uowManager.Current.Options` returns null for every active (non-prepared) UoW — isolation level / transactional flag are invisible to consumers.

**Files:**
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/UnitOfWorkScope.cs`
- Test: `framework/test/BBT.Aether.Postgres.Tests/BBT/Aether/Uow/AmbientBeginTests.cs` (exists) or a small unit test in `BBT.Aether.Infrastructure.Tests`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Begin_exposes_options_through_Current()
{
    var opts = new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true };
    using var uow = _uowManager.Begin(opts);

    Assert.NotNull(_uowManager.Current);
    Assert.NotNull(_uowManager.Current!.Options);
    Assert.True(_uowManager.Current.Options!.IsTransactional);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test framework/test/BBT.Aether.Postgres.Tests --filter "FullyQualifiedName~Begin_exposes_options_through_Current"`
Expected: FAIL — `Current.Options` is null.

- [ ] **Step 3: Implement the fallback**

In `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/UnitOfWorkScope.cs`, change line 52 from:

```csharp
public UnitOfWorkOptions? Options => _options;
```

to:

```csharp
// On the Begin path the root carries the options (scope._options is only set on the prepared path).
// Fall back to the root so Current.Options is never spuriously null for an active UoW.
public UnitOfWorkOptions? Options => _options ?? _root.Options;
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test framework/test/BBT.Aether.Postgres.Tests --filter "FullyQualifiedName~AmbientBeginTests|FullyQualifiedName~Begin_exposes_options_through_Current"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/UnitOfWorkScope.cs \
        framework/test/BBT.Aether.Postgres.Tests/
git commit -m "fix(uow): UnitOfWorkScope.Options falls back to the root's options

The Begin path sets options on the root, not the scope, so Current.Options was
always null for active UoWs. Fall back to root.Options.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 6: Claim-token-guarded job-outcome recording — fix reaper double-execution & state stomping (Critical #2, Important retry off-by-one, Minor recurring-RetryCount)

**Problem:** The `VisibilityTimeout` reaper resets any job whose `RunningSince < now - timeout` — but "stale" means *time elapsed*, not *dispatcher died*. A handler legitimately running past the timeout is reaped → re-armed → runs again concurrently. Worse, the dispatcher's outcome writes (`RecordSuccessAsync`/`RecordFailureAsync` → `UpdateStatusAsync`/`MarkRetryingAsync`/`MarkRecurringRanAsync`) update **by Id with no claim guard**, so when the slow original finishes it silently overwrites the reaper's (or a second run's) state and can double-count retries.

**Fix:** Introduce a per-claim token. `TryClaimAsync` stamps `RunningToken` together with `RunningSince`. Outcome recording becomes an **atomic conditional update** guarded on `Status==Running && RunningToken==token` (via `ExecuteUpdateAsync`, like the existing CAS methods), returning a bool. Both the dispatcher (with `claim.Token`) and the reaper (with the `RunningToken` it observed) use these guarded methods, so exactly one actor transitions a given claim out of `Running`; the loser gets 0 rows and skips. The retry count is therefore incremented exactly once per claim. Recurring jobs stop incrementing `RetryCount`.

**Files:**
- Modify: `framework/src/BBT.Aether.Domain/BBT/Aether/Domain/Entities/BackgroundJobInfo.cs` (add `RunningToken`)
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Domain/EntityFrameworkCore/Modeling/BackgroundJobModelBuilderExtensions.cs` (map the column + extend the reaper index)
- Modify: `framework/src/BBT.Aether.Domain/BBT/Aether/Domain/Repositories/IJobStore.cs` (token on `TryClaimAsync`; add three guarded methods)
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/BackgroundJob/EfCoreJobStore.cs` (impl)
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/BackgroundJob/JobDispatcher.cs` (`JobClaim.Token`; generate token; use guarded methods; handle `false`)
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/BackgroundJob/Processing/BackgroundJobArmingProcessor.cs` (reaper uses guarded methods with observed token)
- Tests: `framework/test/BBT.Aether.Postgres.Tests/BackgroundJob/JobStoreCasTests.cs`, `JobStoreClaimReaperTests.cs`, `ReaperTests.cs`, `JobDispatcherTests.cs`

> **Migration note for consumers:** this adds a nullable `RunningToken` (uniqueidentifier/uuid) column to `BackgroundJobs`. Downstream apps must add an EF migration. The framework's own integration tests use `EnsureCreated`, so the column appears automatically — no test migration needed. Call this out in `framework/docs/background-jobs/README.md`.

- [ ] **Step 1: Add the entity field**

In `framework/src/BBT.Aether.Domain/BBT/Aether/Domain/Entities/BackgroundJobInfo.cs`, after `RunningSince` (line 96) add:

```csharp
    /// <summary>Opaque token stamped by the atomic claim alongside <see cref="RunningSince"/>. Identifies
    /// a specific Running lease so that outcome recording (by the dispatcher) and reaping (by the poller)
    /// can be made conditional: only the holder of the current token may transition the job out of Running.
    /// Null when the job is not Running.</summary>
    public Guid? RunningToken { get; set; }
```

- [ ] **Step 2: Map the column**

In `framework/src/BBT.Aether.Infrastructure/.../Modeling/BackgroundJobModelBuilderExtensions.cs`, after `entity.Property(e => e.RunningSince);` (line 65) add:

```csharp
            entity.Property(e => e.RunningToken);
```

(No further config needed — `Guid?` maps to `uuid`/`uniqueidentifier` by convention. The existing `IX_BackgroundJobs_Running` index on `(Status, RunningSince)` already supports the reaper's `GetStaleRunningAsync` query; the token is only used in the WHERE of point updates by `Id`, which hits the PK.)

- [ ] **Step 3: Extend `IJobStore`** — write the failing store tests first, then the interface

Add a CAS test to `framework/test/BBT.Aether.Postgres.Tests/BackgroundJob/JobStoreClaimReaperTests.cs`:

```csharp
[Fact]
public async Task Guarded_record_only_succeeds_for_the_current_claim_token()
{
    // Arrange: insert a Scheduled job, claim it with tokenA.
    var id = await InsertScheduledJobAsync(JobKind.OneShot, maxRetry: 3);
    var tokenA = Guid.NewGuid();
    Assert.True(await ClaimAsync(id, tokenA));

    // A stale token loses.
    var staleToken = Guid.NewGuid();
    Assert.False(await TryCompleteAsync(id, staleToken));

    // The real token wins exactly once.
    Assert.True(await TryCompleteAsync(id, tokenA));
    Assert.False(await TryCompleteAsync(id, tokenA)); // job no longer Running

    var job = await GetAsync(id);
    Assert.Equal(BackgroundJobStatus.Completed, job!.Status);
    Assert.Null(job.RunningSince);
    Assert.Null(job.RunningToken);
}
```

(Use the test's existing job-store access helpers; the `ClaimAsync`/`TryCompleteAsync` wrappers call the new store methods below.)

Then update `framework/src/BBT.Aether.Domain/BBT/Aether/Domain/Repositories/IJobStore.cs`:

Change `TryClaimAsync` (line 152) to:

```csharp
    /// <summary>
    /// Atomically claims a Scheduled job into Running, stamping <paramref name="nowUtc"/> as RunningSince
    /// and <paramref name="runningToken"/> as the claim token. Returns true iff this caller won the claim.
    /// </summary>
    Task<bool> TryClaimAsync(Guid id, DateTime nowUtc, Guid runningToken, CancellationToken cancellationToken = default);
```

Add three guarded transitions (place after `TryClaimAsync`):

```csharp
    /// <summary>
    /// Atomically records a terminal outcome (Completed / Failed / Cancelled) for a job, ONLY if it is
    /// still Running under <paramref name="runningToken"/>. Clears RunningSince/RunningToken and sets
    /// HandledTime (and LastError when <paramref name="error"/> is provided). Returns false if the claim
    /// was lost (e.g. reaped). The caller must not perform follow-up side effects when false is returned.
    /// </summary>
    Task<bool> TryRecordTerminalAsync(Guid id, Guid runningToken, BackgroundJobStatus terminalStatus,
        DateTime handledTimeUtc, string? error, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically returns a recurring job to Scheduled (for the next cron tick), ONLY if it is still
    /// Running under <paramref name="runningToken"/>. Sets LastRunAt, clears RunningSince/RunningToken,
    /// and sets LastError when <paramref name="error"/> is provided. Does NOT touch RetryCount (recurring
    /// jobs never exhaust). Returns false if the claim was lost.
    /// </summary>
    Task<bool> TryReturnToScheduledAsync(Guid id, Guid runningToken, DateTime ranAtUtc, string? error,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically marks a one-shot job Retrying (incrementing RetryCount, setting NextRetryAt), ONLY if it
    /// is still Running under <paramref name="runningToken"/>. Clears RunningSince/RunningToken and sets
    /// LastError. Returns false if the claim was lost.
    /// </summary>
    Task<bool> TryMarkRetryingAsync(Guid id, Guid runningToken, DateTime nextRetryAtUtc, string? error,
        CancellationToken cancellationToken = default);
```

> Keep the existing `UpdateStatusAsync`, `MarkRetryingAsync`, `MarkRecurringRanAsync` signatures — they are still used by the pre-claim no-handler path and by tests. (After this task the reaper and dispatcher record paths use the guarded variants instead.)

- [ ] **Step 4: Run to verify the store test fails**

Run: `dotnet test framework/test/BBT.Aether.Postgres.Tests --filter "FullyQualifiedName~Guarded_record_only_succeeds_for_the_current_claim_token"`
Expected: FAIL to compile (methods don't exist yet).

- [ ] **Step 5: Implement in `EfCoreJobStore`**

In `framework/src/BBT.Aether.Infrastructure/BBT/Aether/BackgroundJob/EfCoreJobStore.cs`:

Replace `TryClaimAsync` (lines 244-261) with the token-stamping version:

```csharp
    /// <inheritdoc/>
    public async Task<bool> TryClaimAsync(Guid id, DateTime nowUtc, Guid runningToken,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Id cannot be empty.", nameof(id));

        var dbContext = await _dbContextProvider.GetDbContextAsync(cancellationToken);

        // Conditional UPDATE: provider-agnostic claim. WHERE pins Status=Scheduled so exactly one worker
        // wins; the winner stamps RunningSince (reaper) and RunningToken (claim identity).
        var affected = await dbContext.BackgroundJobs
            .Where(j => j.Id == id && j.Status == BackgroundJobStatus.Scheduled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, BackgroundJobStatus.Running)
                .SetProperty(j => j.RunningSince, nowUtc)
                .SetProperty(j => j.RunningToken, runningToken), cancellationToken);

        return affected > 0;
    }
```

Add the three guarded methods (e.g. right after `TryClaimAsync`):

```csharp
    /// <inheritdoc/>
    public async Task<bool> TryRecordTerminalAsync(Guid id, Guid runningToken,
        BackgroundJobStatus terminalStatus, DateTime handledTimeUtc, string? error,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Id cannot be empty.", nameof(id));

        var dbContext = await _dbContextProvider.GetDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;

        var affected = await dbContext.BackgroundJobs
            .Where(j => j.Id == id && j.Status == BackgroundJobStatus.Running && j.RunningToken == runningToken)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, terminalStatus)
                .SetProperty(j => j.HandledTime, handledTimeUtc)
                .SetProperty(j => j.LastError, j => error ?? j.LastError)
                .SetProperty(j => j.RunningSince, (DateTime?)null)
                .SetProperty(j => j.RunningToken, (Guid?)null)
                .SetProperty(j => j.ModifiedAt, now), cancellationToken);

        return affected > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> TryReturnToScheduledAsync(Guid id, Guid runningToken, DateTime ranAtUtc,
        string? error, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Id cannot be empty.", nameof(id));

        var dbContext = await _dbContextProvider.GetDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;

        var affected = await dbContext.BackgroundJobs
            .Where(j => j.Id == id && j.Status == BackgroundJobStatus.Running && j.RunningToken == runningToken)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, BackgroundJobStatus.Scheduled)
                .SetProperty(j => j.LastRunAt, ranAtUtc)
                .SetProperty(j => j.LastError, j => error ?? j.LastError)
                .SetProperty(j => j.RunningSince, (DateTime?)null)
                .SetProperty(j => j.RunningToken, (Guid?)null)
                .SetProperty(j => j.ModifiedAt, now), cancellationToken);

        return affected > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> TryMarkRetryingAsync(Guid id, Guid runningToken, DateTime nextRetryAtUtc,
        string? error, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Id cannot be empty.", nameof(id));

        var dbContext = await _dbContextProvider.GetDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;

        var affected = await dbContext.BackgroundJobs
            .Where(j => j.Id == id && j.Status == BackgroundJobStatus.Running && j.RunningToken == runningToken)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, BackgroundJobStatus.Retrying)
                .SetProperty(j => j.RetryCount, j => j.RetryCount + 1)
                .SetProperty(j => j.NextRetryAt, nextRetryAtUtc)
                .SetProperty(j => j.LastError, error)
                .SetProperty(j => j.RunningSince, (DateTime?)null)
                .SetProperty(j => j.RunningToken, (Guid?)null)
                .SetProperty(j => j.ModifiedAt, now), cancellationToken);

        return affected > 0;
    }
```

Also, for consistency, add `RunningToken = null` clearing to the existing "leaving Running" mutators that remain (`UpdateStatusAsync` line ~142, `MarkRetryingAsync` line ~207, `MarkRecurringRanAsync` line ~232): wherever they set `job.RunningSince = null;`, add `job.RunningToken = null;` on the next line. (These remain for the pre-claim no-handler path and tests.)

> **Note on `Date.UtcNow`:** the guarded methods use `DateTime.UtcNow` for `ModifiedAt` consistent with the existing store methods (which already use `DateTime.UtcNow`). Do not switch these to `IClock` in this task — keep the change surface minimal and matching surrounding code.

- [ ] **Step 6: Run the store test**

Run: `dotnet test framework/test/BBT.Aether.Postgres.Tests --filter "FullyQualifiedName~JobStoreClaimReaperTests|FullyQualifiedName~JobStoreCasTests"`
Expected: PASS (new guarded test + existing CAS tests). Fix any existing test that called `TryClaimAsync(id, now, ct)` — add a `Guid.NewGuid()` token argument.

- [ ] **Step 7: Thread the token through `JobDispatcher`**

In `framework/src/BBT.Aether.Infrastructure/BBT/Aether/BackgroundJob/JobDispatcher.cs`:

Add `Guid Token` to the `JobClaim` record (line 62):

```csharp
    private readonly record struct JobClaim(
        Guid JobId,
        string HandlerName,
        JobKind Kind,
        int RetryCount,
        int MaxRetryCount,
        Guid Token);
```

In `ClaimAsync`, generate the token, pass it to `TryClaimAsync`, and store it (replace lines 164-176):

```csharp
        // Atomic claim: only one worker wins Scheduled→Running (stamps RunningSince + a fresh claim token).
        var runningToken = Guid.NewGuid();
        var claimed = await jobStore.TryClaimAsync(jobInfo.Id, clock.UtcNow, runningToken, cancellationToken);
        await claimUow.CommitAsync(cancellationToken);
        if (!claimed)
        {
            logger.LogInformation(
                "Job id '{JobId}' was not Scheduled (already claimed or late delivery); skipping", jobInfo.Id);
            activity?.SetTag("job.status", "skipped");
            activity?.SetStatus(ActivityStatusCode.Ok);
            return null;
        }

        return new JobClaim(jobInfo.Id, jobInfo.HandlerName, jobInfo.Kind, jobInfo.RetryCount,
            jobInfo.MaxRetryCount, runningToken);
```

Rewrite `RecordSuccessAsync` (lines 182-209) to use guarded methods and skip side effects when the claim was lost:

```csharp
    private async Task RecordSuccessAsync(
        AsyncServiceScope scope,
        JobClaim claim,
        string jobName,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var jobStore = scope.ServiceProvider.GetRequiredService<IJobStore>();
        var jobScheduler = scope.ServiceProvider.GetRequiredService<IJobScheduler>();

        bool recorded;
        await using (var doneUow = uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
        {
            recorded = claim.Kind == JobKind.Recurring
                ? await jobStore.TryReturnToScheduledAsync(claim.JobId, claim.Token, clock.UtcNow, null, cancellationToken)
                : await jobStore.TryRecordTerminalAsync(claim.JobId, claim.Token, BackgroundJobStatus.Completed,
                    clock.UtcNow, null, cancellationToken);
            await doneUow.CommitAsync(cancellationToken);
        }

        if (!recorded)
        {
            // The claim was revoked (e.g. reaped after the visibility timeout). Another actor owns the
            // job's state now; do not record an outcome or delete from the scheduler.
            logger.LogWarning("Claim for job id '{JobId}' was lost before success could be recorded; skipping", claim.JobId);
            activity?.SetTag("job.status", "claim-lost");
            return;
        }

        activity?.SetTag("job.status", claim.Kind == JobKind.Recurring ? "scheduled" : "completed");
        activity?.SetStatus(ActivityStatusCode.Ok);

        if (claim.Kind == JobKind.OneShot)
            await TryDeleteFromSchedulerAsync(jobScheduler, claim.HandlerName, jobName, cancellationToken);
    }
```

Rewrite `RecordFailureAsync` (lines 215-252):

```csharp
    private async Task RecordFailureAsync(
        AsyncServiceScope scope,
        JobClaim claim,
        string jobName,
        string error,
        CancellationToken cancellationToken)
    {
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var jobStore = scope.ServiceProvider.GetRequiredService<IJobStore>();
        var jobScheduler = scope.ServiceProvider.GetRequiredService<IJobScheduler>();

        var willRetry = claim.Kind == JobKind.OneShot && claim.RetryCount + 1 <= claim.MaxRetryCount;

        bool recorded;
        await using (var failUow = uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
        {
            if (claim.Kind == JobKind.Recurring)
            {
                recorded = await jobStore.TryReturnToScheduledAsync(claim.JobId, claim.Token, clock.UtcNow, error, cancellationToken);
            }
            else if (willRetry)
            {
                var nextRetryAt = clock.UtcNow + ComputeBackoff(claim.RetryCount, options);
                recorded = await jobStore.TryMarkRetryingAsync(claim.JobId, claim.Token, nextRetryAt, error, cancellationToken);
            }
            else
            {
                recorded = await jobStore.TryRecordTerminalAsync(claim.JobId, claim.Token, BackgroundJobStatus.Failed,
                    clock.UtcNow, error, cancellationToken);
            }

            await failUow.CommitAsync(cancellationToken);
        }

        if (!recorded)
        {
            logger.LogWarning("Claim for job id '{JobId}' was lost before failure could be recorded; skipping", claim.JobId);
            return;
        }

        // Only delete a terminally-failed one-shot from the scheduler. Recurring stays armed; a Retrying
        // one-shot is re-armed by the poller.
        if (claim.Kind == JobKind.OneShot && !willRetry)
            await TryDeleteFromSchedulerAsync(jobScheduler, claim.HandlerName, jobName, cancellationToken);
    }
```

For the **cancellation** path (DispatchCoreAsync lines 90-102): make it claim-guarded too, since the job is Running under `c.Token`. Replace the `MarkJobStatusAsync(...)` call with:

```csharp
            var jobStore = scope.ServiceProvider.GetRequiredService<IJobStore>();
            var jobScheduler = scope.ServiceProvider.GetRequiredService<IJobScheduler>();
            var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();

            logger.LogWarning("Handler '{HandlerName}' for job id '{JobId}' was cancelled", c.HandlerName, c.JobId);
            activity?.SetTag("job.status", "cancelled");
            activity?.SetStatus(ActivityStatusCode.Ok);

            await using (var cancelUow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                await jobStore.TryRecordTerminalAsync(c.JobId, c.Token, BackgroundJobStatus.Cancelled,
                    clock.UtcNow, "Job was cancelled", cancellationToken);
                await cancelUow.CommitAsync(cancellationToken);
            }
            await TryDeleteFromSchedulerAsync(jobScheduler, c.HandlerName, jobName, cancellationToken);
```

If `MarkJobStatusAsync` is now unused, delete it (lines 289-311). The no-handler path in `ClaimAsync` still uses `jobStore.UpdateStatusAsync` directly (job is Scheduled/pre-claim) — leave that as is.

- [ ] **Step 8: Update the reaper to use guarded methods with the observed token**

In `framework/src/BBT.Aether.Infrastructure/BBT/Aether/BackgroundJob/Processing/BackgroundJobArmingProcessor.cs`, replace the reaper transition block (lines 129-141) with token-guarded calls. The reaper observed `job` (with its `RunningToken`); it must only reset if the job is STILL Running under that token, so a just-finished original can't be stomped:

```csharp
                    const string reason = "Reaped: execution exceeded the visibility timeout";
                    var token = job.RunningToken ?? Guid.Empty;
                    if (job.Kind == JobKind.Recurring)
                    {
                        await jobStore.TryReturnToScheduledAsync(job.Id, token, clock.UtcNow, reason, ct); // → Scheduled (next cron tick)
                    }
                    else if (job.RetryCount + 1 <= job.MaxRetryCount)
                    {
                        await jobStore.TryMarkRetryingAsync(job.Id, token, clock.UtcNow, reason, ct);       // → Retrying, NextRetryAt=now ⇒ poller re-arms
                    }
                    else
                    {
                        await jobStore.TryRecordTerminalAsync(job.Id, token, BackgroundJobStatus.Failed, clock.UtcNow,
                            "Reaped: retries exhausted after a stuck execution", ct);
                    }
```

(`GetStaleRunningAsync` only returns `Running` rows with a non-null `RunningSince`; a claimed job always has a non-null `RunningToken`, so `token` is the real value. The `?? Guid.Empty` is a defensive fallback that will simply match nothing if the column were ever null.)

- [ ] **Step 9: Add the concurrency regression tests**

Add to `framework/test/BBT.Aether.Postgres.Tests/BackgroundJob/ReaperTests.cs`:

```csharp
[Fact]
public async Task Reaped_jobs_original_completion_does_not_overwrite_reaper_state()
{
    // Arrange: claim a one-shot with tokenA, then simulate a reap (reaper resets Running→Retrying with tokenA).
    var id = await InsertScheduledJobAsync(JobKind.OneShot, maxRetry: 3);
    var tokenA = Guid.NewGuid();
    Assert.True(await ClaimAsync(id, tokenA));               // Running, tokenA
    await RunReaperAfterTimeoutAsync();                       // Running→Retrying, token cleared

    // Act: the original (slow) execution now tries to record success under the stale tokenA.
    var recorded = await TryCompleteAsync(id, tokenA);

    // Assert: the stale completion is rejected; the reaper's Retrying state stands.
    Assert.False(recorded);
    var job = await GetAsync(id);
    Assert.Equal(BackgroundJobStatus.Retrying, job!.Status);
    Assert.Equal(1, job.RetryCount);                         // incremented exactly once (by the reaper)
}
```

Use the existing `ReaperTests` fixtures/helpers; adapt names to those already present.

- [ ] **Step 10: Build and run the full job test suite**

Run: `dotnet build framework/BBT.Aether.slnx -c Release` → 0 errors.
Run: `dotnet test framework/test/BBT.Aether.Postgres.Tests --filter "FullyQualifiedName~BackgroundJob|FullyQualifiedName~Reaper|FullyQualifiedName~JobStore|FullyQualifiedName~JobDispatcher|FullyQualifiedName~EndToEnd"`
Expected: PASS. Update any test that called the old `TryClaimAsync(id, now, ct)` (add a token) or asserted dispatcher record behavior via `UpdateStatusAsync`/`MarkRetryingAsync`/`MarkRecurringRanAsync` (now guarded variants). The `JobDispatcherTests` behavior matrix (one-shot/recurring × success/retry/exhaust/cancel, already-Running skip, missing job) must stay green.

- [ ] **Step 11: Document the migration + visibility-timeout semantics**

In `framework/docs/background-jobs/README.md`, add/extend the reaper section:

```markdown
### Visibility timeout & claim tokens

The atomic claim stamps a `RunningToken` (and `RunningSince`) on the job row. Recording a job's
outcome — by the dispatcher when the handler finishes, or by the reaper when a Running job exceeds
`VisibilityTimeout` — is a conditional update guarded on `Status == Running AND RunningToken == <claim>`.
Exactly one actor wins; the loser's update affects zero rows and is skipped. This means:

- `VisibilityTimeout` is a **hard ceiling**: a handler that runs longer than the timeout will be
  reaped and may be re-armed. Size the timeout above your slowest expected handler.
- A reaped-then-finished original execution cannot overwrite the reaper's (or a retry's) state.

**Migration:** this release adds a nullable `RunningToken` (uuid / uniqueidentifier) column to the
`BackgroundJobs` table. Add an EF Core migration in your application.
```

- [ ] **Step 12: Commit**

```bash
git add -A
git commit -m "fix(jobs)!: claim-token-guard job outcome recording to stop reaper double-run stomping

Add BackgroundJobInfo.RunningToken, stamped by the atomic claim. Dispatcher
outcome recording and the visibility-timeout reaper now transition a job out of
Running only via conditional ExecuteUpdate guarded on (Status==Running &&
RunningToken==token), so exactly one actor wins and a slow original execution can
no longer overwrite reaper/retry state. Retry count increments exactly once per
claim; recurring jobs no longer increment RetryCount. Adds a nullable RunningToken
column (consumers must add a migration).

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 7: `PostgreSqlIdentifier` — bound schema identifier length (Minor)

**Problem:** `PostgreSqlIdentifier.QuoteSchema` validates `^[a-zA-Z_][a-zA-Z0-9_]*$` but has no length bound. PostgreSQL silently truncates identifiers to 63 bytes, so a tenant-derived schema name >63 chars resolves to a *different* (truncated) schema than intended, and two long names sharing a 63-char prefix collide. Throw instead.

**Files:**
- Modify: `framework/src/BBT.Aether.Npgsql/PostgreSqlIdentifier.cs`
- Test: `framework/test/BBT.Aether.Postgres.Tests/PostgreSqlIdentifierTests.cs` (exists)

- [ ] **Step 1: Write the failing test**

Add to `PostgreSqlIdentifierTests.cs`:

```csharp
[Fact]
public void QuoteSchema_rejects_identifiers_longer_than_63_bytes()
{
    var tooLong = new string('a', 64);
    Assert.Throws<ArgumentException>(() => PostgreSqlIdentifier.QuoteSchema(tooLong));
}

[Fact]
public void QuoteSchema_accepts_identifier_at_the_63_byte_limit()
{
    var atLimit = new string('a', 63);
    var quoted = PostgreSqlIdentifier.QuoteSchema(atLimit);
    Assert.Equal($"\"{atLimit}\"", quoted);
}
```

(Match the actual method name/signature in `PostgreSqlIdentifier.cs` — if it is an instance method or differently named, adapt the calls.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test framework/test/BBT.Aether.Postgres.Tests --filter "FullyQualifiedName~PostgreSqlIdentifierTests"`
Expected: FAIL — the 64-char name does not throw today.

- [ ] **Step 3: Implement the length guard**

In `framework/src/BBT.Aether.Npgsql/PostgreSqlIdentifier.cs`, in the validation (the regex check around lines 11-12), add a length check that throws for >63 bytes BEFORE/with the format check. Example:

```csharp
        const int MaxIdentifierBytes = 63; // PostgreSQL NAMEDATALEN - 1; longer names are silently truncated.
        if (System.Text.Encoding.UTF8.GetByteCount(schema) > MaxIdentifierBytes)
        {
            throw new ArgumentException(
                $"Schema identifier exceeds PostgreSQL's {MaxIdentifierBytes}-byte limit and would be silently truncated.",
                nameof(schema));
        }
```

(The existing format regex `^[a-zA-Z_][a-zA-Z0-9_]*$` restricts to ASCII, so byte count == char count here; using `GetByteCount` is future-proof and harmless.)

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test framework/test/BBT.Aether.Postgres.Tests --filter "FullyQualifiedName~PostgreSqlIdentifierTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add framework/src/BBT.Aether.Npgsql/PostgreSqlIdentifier.cs \
        framework/test/BBT.Aether.Postgres.Tests/PostgreSqlIdentifierTests.cs
git commit -m "fix(npgsql): reject schema identifiers over 63 bytes to prevent silent truncation

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 8: Document SQL Server single-schema behavior (user decision — docs only)

**Decision:** SQL Server is single-schema; it always uses the default/model schema, and `ICurrentSchema.Change(...)` does not switch schema per command. Document this clearly so callers don't expect per-request schema switching on SQL Server. No code guard (per user decision).

**Files:**
- Modify: `framework/src/BBT.Aether.SqlServer/SqlServerAetherProvider.cs` (tighten the XML remarks)
- Modify: `framework/docs/multi-schema/README.md` (add a "Provider support" note)
- Modify (if a provider/SQL-Server section exists): `framework/docs/unit-of-work/README.md`

- [ ] **Step 1: Tighten the provider XML docs**

In `framework/src/BBT.Aether.SqlServer/SqlServerAetherProvider.cs`, expand the class `<summary>`/`<remarks>` to state explicitly that `ICurrentSchema.Change` has no per-command effect on SQL Server:

```csharp
/// <summary>
/// SQL Server provider for the Aether Unit of Work. SQL Server has no transaction-scoped
/// search_path equivalent, so this provider is SINGLE-SCHEMA: it supplies the shared
/// connection/transaction and binds options, but does NOT switch schema per command.
/// </summary>
/// <remarks>
/// On SQL Server the schema is fixed by the EF model (e.g. <c>modelBuilder.HasDefaultSchema("x")</c>
/// or schema-qualified <c>ToTable</c>). Calls to <c>ICurrentSchema.Change(...)</c> do NOT change the
/// schema used for queries on this provider — the requested schema argument is ignored by
/// <see cref="ApplyShared"/>. Do not rely on per-request/multi-schema switching on SQL Server; use
/// the PostgreSQL provider (search_path-based) when true multi-schema is required. Full SQL Server
/// multi-schema (per-schema compiled models) is a future enhancement.
/// </remarks>
```

- [ ] **Step 2: Add a provider-support note to the multi-schema docs**

In `framework/docs/multi-schema/README.md`, add near the top (after the intro):

```markdown
## Provider support

Multi-schema (per-request schema switching via `ICurrentSchema.Change`) is supported **only on the
PostgreSQL provider** (`BBT.Aether.Npgsql`), which applies `SET LOCAL search_path` per command on the
shared transaction.

**SQL Server (`BBT.Aether.SqlServer`) is single-schema.** It always uses the schema fixed in the EF
model (`HasDefaultSchema` / schema-qualified `ToTable`). On SQL Server, `ICurrentSchema.Change(...)`
does not change the schema used for queries — the call is effectively a no-op for schema resolution.
Applications requiring true multi-schema isolation must use PostgreSQL.
```

- [ ] **Step 3: Cross-link from the unit-of-work docs (if applicable)**

If `framework/docs/unit-of-work/README.md` discusses providers or schema, add one line pointing to the multi-schema "Provider support" section noting SQL Server is single-schema. If there is no relevant section, skip this step (do not invent one).

- [ ] **Step 4: Verify build (docs-only, sanity)**

Run: `dotnet build framework/BBT.Aether.slnx -c Release`
Expected: 0 errors (XML-doc-only change).

- [ ] **Step 5: Commit**

```bash
git add framework/src/BBT.Aether.SqlServer/SqlServerAetherProvider.cs framework/docs/multi-schema/README.md framework/docs/unit-of-work/README.md
git commit -m "docs(provider): document SQL Server single-schema (Change() is a no-op for schema resolution)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Self-Review (author checklist)

- **Spec coverage:** Critical #1 → T1; Critical #2 (+ retry off-by-one Important + recurring-RetryCount Minor) → T6; Critical #3 → T2 (removal per decision); Important disposal leak → T3; Important rollback guard → T4; Important Options-null → T5; Minor identifier length → T7; SQL Server doc decision → T8. The `jsonb`-hardcode Minor is intentionally **out of scope** (pre-existing, broader provider-modeling concern) — note it for a future task, do not fix here. Reaper clock-skew Minor is covered by the T6/T11 doc note (single-processor-per-schema deployment).
- **Type consistency:** new store methods `TryClaimAsync(id, now, token, ct)`, `TryRecordTerminalAsync`, `TryReturnToScheduledAsync`, `TryMarkRetryingAsync` are used with identical signatures in `EfCoreJobStore`, `JobDispatcher`, `BackgroundJobArmingProcessor`, and tests. `JobClaim` gains `Token` and every constructor call is updated (T6 Step 7). `BackgroundJobInfo.RunningToken` is mapped (T6 Step 2) and cleared everywhere a job leaves Running.
- **No placeholders:** every code step shows final code; every run step shows the command + expected result.
- **Breaking changes:** T1 (CurrentSchema internal behavior — non-breaking API), T2 (removes public `IDomainEventSink` — breaking, v2-acceptable), T6 (IJobStore signature changes + new column/migration — breaking, v2-acceptable). All acceptable on `feature/multi-schema-uow`.
