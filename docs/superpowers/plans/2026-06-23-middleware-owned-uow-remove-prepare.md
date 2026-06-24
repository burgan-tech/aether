# Middleware-Owned UnitOfWork + Remove Prepare — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Steps use `- [ ]`.

**Goal:** Make `UnitOfWorkMiddleware` eagerly begin and own the request UnitOfWork (commit/rollback), make the `[UnitOfWork]` aspect join an existing ambient UoW (Required) or own its own (RequiresNew / no ambient), and remove the Prepare machinery entirely. Direct repository use works without the aspect; the prepared-path commit gap disappears.

**Spec:** `docs/superpowers/specs/2026-06-23-middleware-owned-uow-remove-prepare-design.md` (authoritative).

**Architecture:** Two ordered tasks that each keep the solution compiling. T1 migrates the only two callers (middleware, aspect) off Prepare and adds behavior tests. T2 deletes the now-unused Prepare API across the interfaces and implementors. Then a final review.

**Tech Stack:** .NET 10, EF Core 10, PostSharp (aspect, compile-time woven), xUnit + Shouldly, Testcontainers.PostgreSql.

**Key facts the implementer must know:**
- `CompositeUnitOfWork.Begin`/`InitializeCore` does **no I/O**; connection + transaction open lazily on first `GetDbContextAsync`. Eager `Begin` is cheap.
- `RollbackAsync` (IsCompleted guard) and `DisposeAsync` (early `_isDisposed`, one-shot failed-handlers) are already idempotent — double rollback / rollback-then-dispose is safe.
- `AetherDbContextProvider.GetDbContextAsync` throws `No active UnitOfWork.` when `uowManager.Current` is null. `Current` = `ambient.GetActiveUnitOfWork()` which skips `IsPrepared`/`IsCompleted`/`IsDisposed`.
- Only `UnitOfWorkMiddleware` and `UnitOfWorkAttribute` *call* Prepare/`TryBeginPreparedAsync`. No tests reference Prepare.
- `Begin(Required)` when an active UoW exists returns a participating scope (`ownsRoot=false`); disposing it only restores ambient and does NOT tear down the shared root.

---

## Task 1: Migrate middleware + aspect off Prepare (behavior change)

**Files:**
- Modify: `framework/src/BBT.Aether.AspNetCore/BBT/Aether/AspNetCore/Middleware/UnitOfWorkMiddleware.cs`
- Modify: `framework/src/BBT.Aether.Aspects/BBT/Aether/Aspects/Uow/UnitOfWorkAttribute.cs`
- Test: `framework/test/BBT.Aether.Postgres.Tests/` — new `UnitOfWorkMiddlewareTests.cs` + manager-level join/isolation tests (add to `MultiSchemaUnitOfWorkTests.cs` or a new file). May require adding refs to the Postgres test csproj (see Step 1).

- [ ] **Step 1: Prepare the test project to exercise the middleware**

The middleware lives in `BBT.Aether.AspNetCore` and needs ASP.NET Core types (`HttpContext`, `DefaultHttpContext`, `RequestDelegate`). Check `framework/test/BBT.Aether.Postgres.Tests/BBT.Aether.Postgres.Tests.csproj`. If it does not already reference them, add:

```xml
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <ProjectReference Include="..\..\src\BBT.Aether.AspNetCore\BBT.Aether.AspNetCore.csproj" />
  </ItemGroup>
```

Run `dotnet build framework/test/BBT.Aether.Postgres.Tests -c Release` to confirm it still builds. (If a project reference cycle or framework-reference conflict appears, instead create a minimal new test project `BBT.Aether.AspNetCore.Tests` referencing `BBT.Aether.AspNetCore` + `Microsoft.AspNetCore.App`, and put the middleware test there; report the choice.)

- [ ] **Step 2: Write the failing middleware test**

Create `framework/test/BBT.Aether.Postgres.Tests/UnitOfWorkMiddlewareTests.cs`. Wire an `IUnitOfWorkManager` exactly as `MultiSchemaUnitOfWorkTests.BuildProvider()` does (real Npgsql configurator + the test schema + `ProbeDbContext`/`Thing`). The middleware resolves `IUnitOfWorkManager` from its constructor, so pass the SAME manager instance the `next` delegate uses.

```csharp
[Fact]
public async Task Active_UnitOfWork_is_available_inside_next_for_a_non_excluded_path()
{
    var (mgr, _) = BuildManager(); // returns IUnitOfWorkManager wired like MultiSchemaUnitOfWorkTests
    var mw = new UnitOfWorkMiddleware(mgr, Options.Create(new UnitOfWorkMiddlewareOptions()));

    var ctx = new DefaultHttpContext();
    ctx.Request.Path = "/api/orders"; // NOT excluded ("/" exact-match IS excluded by default)

    bool currentWasActive = false;
    await mw.InvokeAsync(ctx, _ =>
    {
        currentWasActive = mgr.Current is not null; // active, NOT a skipped prepared placeholder
        return Task.CompletedTask;
    });

    currentWasActive.ShouldBeTrue();
}
```

This fails today because the middleware only `Prepare`s → `mgr.Current` is null inside `next`.

Add the persistence + rollback pair (uses the DB fixture; assert via a fresh connection like `CountAsync`):

```csharp
[Fact]
public async Task Middleware_commits_on_success()
{
    await ArrangeSchemaAsync();
    var (mgr, sp) = BuildManager();
    var mw = new UnitOfWorkMiddleware(mgr, Options.Create(new UnitOfWorkMiddlewareOptions()));
    var ctx = new DefaultHttpContext(); ctx.Request.Path = "/api/orders";

    await mw.InvokeAsync(ctx, async _ =>
    {
        var db = await mgr.Current!.As<IEfCoreUnitOfWork>().GetDbContextAsync<ProbeDbContext>(_schema);
        db.Set<Thing>().Add(new Thing { Id = Guid.NewGuid(), Name = "x" });
    });

    (await CountAsync(_schema)).ShouldBe(1); // committed
}

[Fact]
public async Task Middleware_rolls_back_on_exception()
{
    await ArrangeSchemaAsync();
    var (mgr, sp) = BuildManager();
    var mw = new UnitOfWorkMiddleware(mgr, Options.Create(new UnitOfWorkMiddlewareOptions()));
    var ctx = new DefaultHttpContext(); ctx.Request.Path = "/api/orders";

    await Should.ThrowAsync<InvalidOperationException>(async () =>
        await mw.InvokeAsync(ctx, async _ =>
        {
            var db = await mgr.Current!.As<IEfCoreUnitOfWork>().GetDbContextAsync<ProbeDbContext>(_schema);
            db.Set<Thing>().Add(new Thing { Id = Guid.NewGuid(), Name = "x" });
            throw new InvalidOperationException("boom");
        }));

    (await CountAsync(_schema)).ShouldBe(0); // rolled back
}

[Fact]
public async Task Excluded_path_runs_next_without_a_UnitOfWork()
{
    var (mgr, _) = BuildManager();
    var mw = new UnitOfWorkMiddleware(mgr, Options.Create(new UnitOfWorkMiddlewareOptions()));
    var ctx = new DefaultHttpContext(); ctx.Request.Path = "/health";

    bool currentWasNull = false;
    await mw.InvokeAsync(ctx, _ => { currentWasNull = mgr.Current is null; return Task.CompletedTask; });

    currentWasNull.ShouldBeTrue();
}
```

Helper notes for the implementer: factor `BuildManager()` to return a wired `IUnitOfWorkManager` (new `UnitOfWorkManager(new AsyncLocalAmbientUowAccessor(), sp)` where `sp` registers `IAetherDbContextConfigurator<ProbeDbContext>` and `ICurrentSchema` as a `StaticCurrentSchema(_schema)` so `GetDbContextAsync(_schema)` resolves). Reuse `ArrangeSchemasAsync`/`CountAsync`/`ProbeDbContext`/`Thing` patterns from `MultiSchemaUnitOfWorkTests` (copy or share). `.As<IEfCoreUnitOfWork>()` is illustrative — cast as that file does (`(IEfCoreUnitOfWork)mgr.Current`).

- [ ] **Step 3: Run the middleware tests to confirm they fail**

Run: `dotnet test framework/test/BBT.Aether.Postgres.Tests -c Release --filter "FullyQualifiedName~UnitOfWorkMiddlewareTests"`
Expected: FAIL — `Active_UnitOfWork_is_available_inside_next...` (Current null) and the commit test (no commit today).

- [ ] **Step 4: Rewrite the middleware to eager-begin + own**

Replace `InvokeAsync` in `UnitOfWorkMiddleware.cs` with:

```csharp
public async Task InvokeAsync(HttpContext context, RequestDelegate next)
{
    if (!ShouldStartUnitOfWork(context))
    {
        await next(context);
        return;
    }

    // Own the request UnitOfWork: eager Begin (no I/O — connection/transaction open lazily on first
    // repository call), commit on success, roll back on exception. Begin (not Prepare) means a direct
    // repository call works downstream without the [UnitOfWork] aspect.
    await using var uow = _uowManager.Begin(_options.DefaultOptions);
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
}
```

Update the class XML summary to say it *begins/commits/rolls back* (drop "starts placeholder"). Leave `ShouldStartUnitOfWork`/`IsPathMatch`/options untouched. Remove the now-stale `// Prepare UoW ...` comment and the commented-out commit line.

- [ ] **Step 5: Run the middleware tests to confirm they pass**

Run: `dotnet test framework/test/BBT.Aether.Postgres.Tests -c Release --filter "FullyQualifiedName~UnitOfWorkMiddlewareTests"`
Expected: PASS (all four).

- [ ] **Step 6: Write the manager-level aspect-semantics tests**

The aspect is PostSharp-woven and not unit-testable in isolation; its two outcomes reduce to standard manager semantics. Add to `MultiSchemaUnitOfWorkTests.cs` (real DB):

```csharp
[Fact]
public async Task Required_participant_work_is_committed_by_the_owner()
{
    await ArrangeSchemasAsync();
    var sp = BuildProvider();
    var mgr = new UnitOfWorkManager(new AsyncLocalAmbientUowAccessor(), sp);

    await using var owner = mgr.Begin(new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.Required });
    var a = await ((IEfCoreUnitOfWork)mgr.Current!).GetDbContextAsync<ProbeDbContext>(_schemaA);
    a.Set<Thing>().Add(new Thing { Id = Guid.NewGuid(), Name = "owner" });

    await using (var participant = mgr.Begin(new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.Required }))
    {
        var p = await ((IEfCoreUnitOfWork)mgr.Current!).GetDbContextAsync<ProbeDbContext>(_schemaA);
        p.Set<Thing>().Add(new Thing { Id = Guid.NewGuid(), Name = "participant" });
        // participant does NOT commit — mirrors the aspect's join branch
    }

    await owner.CommitAsync();
    (await CountAsync(_schemaA)).ShouldBe(2); // both rows committed by the owner
}

[Fact]
public async Task RequiresNew_commits_independently_of_a_rolled_back_outer()
{
    await ArrangeSchemasAsync();
    var sp = BuildProvider();
    var mgr = new UnitOfWorkManager(new AsyncLocalAmbientUowAccessor(), sp);

    await using var outer = mgr.Begin(new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.Required });
    var a = await ((IEfCoreUnitOfWork)mgr.Current!).GetDbContextAsync<ProbeDbContext>(_schemaA);
    a.Set<Thing>().Add(new Thing { Id = Guid.NewGuid(), Name = "outer" });

    await using (var inner = mgr.Begin(new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew }))
    {
        var i = await ((IEfCoreUnitOfWork)mgr.Current!).GetDbContextAsync<ProbeDbContext>(_schemaB);
        i.Set<Thing>().Add(new Thing { Id = Guid.NewGuid(), Name = "inner" });
        await inner.CommitAsync();
    }

    await outer.RollbackAsync();
    (await CountAsync(_schemaB)).ShouldBe(1); // inner persisted
    (await CountAsync(_schemaA)).ShouldBe(0); // outer discarded
}
```

Run them first to confirm they pass with the CURRENT manager (these assert pre-existing Required/RequiresNew semantics that the aspect will rely on). If `Required_participant...` does not already pass, STOP and report — it means the join semantics need attention before the aspect rewrite.

- [ ] **Step 7: Rewrite the aspect to join-or-own**

Replace `OnInvokeAsync` in `UnitOfWorkAttribute.cs` with:

```csharp
public async override Task OnInvokeAsync(MethodInterceptionArgs args)
{
    await OnBeforeAsync(args);

    var serviceProvider = GetServiceProvider();
    var cancellationToken = ExtractCancellationToken(args);
    var uowManager = serviceProvider.GetRequiredService<IUnitOfWorkManager>();
    var options = CreateOptions();

    // Participate in an existing ambient UnitOfWork (e.g. the request UoW the middleware owns, or an
    // outer aspect). The OWNER commits/rolls back; we only run the method and let exceptions propagate.
    if (options.Scope == UnitOfWorkScopeOption.Required && uowManager.Current is not null)
    {
        try
        {
            await args.ProceedAsync();
            await OnAfterAsync(args);
        }
        catch (Exception ex)
        {
            await OnExceptionAsync(args, ex);
            throw; // owner rolls back
        }

        return;
    }

    // Own a UnitOfWork (RequiresNew, Suppress, or Required with no ambient — e.g. non-HTTP paths). Use the
    // synchronous Begin so the unit of work is ambient in this frame and flows into ProceedAsync.
    await using var uow = uowManager.Begin(options);
    try
    {
        await args.ProceedAsync();
        await uow.CommitAsync(cancellationToken);
        await OnAfterAsync(args);
    }
    catch (Exception ex)
    {
        await uow.RollbackAsync(cancellationToken);
        await OnExceptionAsync(args, ex);
        throw;
    }
}
```

(`OnInvoke` sync bridge, `CreateOptions`, extensibility hooks unchanged.) Note: this still references `UnitOfWorkOptions.PrepareName`? No — it must NOT. Confirm the `TryBeginPreparedAsync` branch and the `using BBT...PrepareName` references are gone.

- [ ] **Step 8: Build + run the full affected suites**

Run: `dotnet build framework/BBT.Aether.slnx -c Release` → 0 errors.
Run: `dotnet test framework/test/BBT.Aether.Postgres.Tests -c Release` and `dotnet test framework/test/BBT.Aether.Infrastructure.Tests -c Release` → green.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat(uow)!: middleware owns an eager request UnitOfWork; aspect joins or owns

UnitOfWorkMiddleware now Begins (not Prepares) the request UnitOfWork and owns its
commit/rollback, so a direct repository call works without the [UnitOfWork] aspect and
writes actually persist. The aspect participates in an ambient Required UoW (owner
commits) or owns its own for RequiresNew / non-HTTP paths. Prepare is no longer used.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: Remove the Prepare API (dead-code removal)

After Task 1 nothing calls Prepare. Delete it across the interfaces and implementors. Each edit is mechanical; the solution must compile with 0 errors and all existing tests stay green.

**Files & exact removals:**

- [ ] **Step 1: Core interfaces**
  - `framework/src/BBT.Aether.Core/BBT/Aether/Uow/IUnitOfWorkManager.cs` — remove `Prepare(string, bool)` and `TryBeginPreparedAsync(...)`.
  - `framework/src/BBT.Aether.Core/BBT/Aether/Uow/IUnitOfWork.cs` — remove `Prepare(string)`, `IsPrepared`, `IsPreparedFor(string)`, `PreparationName`. Inspect the sync `Initialize(UnitOfWorkOptions)` member: if its only callers were the prepared flow (now gone), remove it too; keep `InitializeAsync`. (Grep `\.Initialize(` across `framework/src` + `framework/test` first; remove only if unreferenced.)
  - `framework/src/BBT.Aether.Core/BBT/Aether/Uow/UnitOfWorkOptions.cs` — remove `public const string PrepareName = "AetherUow";`.

- [ ] **Step 2: Implementors**
  - `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/UnitOfWorkManager.cs` — remove `Prepare(...)` and `TryBeginPreparedAsync(...)`.
  - `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/UnitOfWorkScope.cs` — remove `_isPrepared`, `_preparationName`, `_options` fields; `Prepare`, `Initialize`, `IsPrepared`, `IsPreparedFor`, `PreparationName` members; and every `if (_isPrepared) { ... }` guard in `GetDbContextAsync` / `SaveChangesAsync` / `CommitAsync` / `RollbackAsync` (those methods now just delegate to `_root`). Change `Options => _options ?? _root.Options` to `Options => _root.Options`. Keep the `ownsRoot` disposal logic untouched.
  - `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/CompositeUnitOfWork.cs` — remove `Prepare` (throws), `IsPrepared => false`, `PreparationName => null`, `IsPreparedFor => false`. Remove the sync `Initialize(UnitOfWorkOptions)` throw-stub iff `IUnitOfWork.Initialize` was removed in Step 1; keep `InitializeAsync` + `InitializeCore`.
  - `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/SuppressedUowScope.cs` — remove `Prepare`, `IsPrepared`, `IsPreparedFor`, `PreparationName` (and `Initialize` if removed from the interface).
  - `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/AsyncLocalAmbientUowAccessor.cs` — change the skip loop from `while (uow != null && (uow.IsPrepared || uow.IsCompleted || uow.IsDisposed))` to `while (uow != null && (uow.IsCompleted || uow.IsDisposed))`.

- [ ] **Step 2.5: Verify there is genuinely no caller left**

Run: `grep -rn "Prepare\|TryBeginPrepared\|IsPrepared\|PreparationName\|PrepareName\|IsPreparedFor" framework/src framework/test --include="*.cs" | grep -v /obj/`
Expected: empty (XML-doc mentions also gone). Fix any straggler.

- [ ] **Step 3: Build + full test suite**

Run: `dotnet build framework/BBT.Aether.slnx -c Release` → 0 errors.
Run: `dotnet test framework/test/BBT.Aether.Infrastructure.Tests -c Release`, `dotnet test framework/test/BBT.Aether.Postgres.Tests -c Release`, `dotnet test framework/test/BBT.Aether.SqlServer.Tests -c Release` → all green.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "refactor(uow)!: remove the Prepare/TryBeginPrepared reserve-activation API

The middleware now eagerly owns the request UnitOfWork and the aspect joins/owns it,
so the prepared-placeholder activation pattern is dead. Remove Prepare, TryBeginPreparedAsync,
IsPrepared, IsPreparedFor, PreparationName, PrepareName, and the IsPrepared skip in the
ambient accessor across IUnitOfWork(Manager), UnitOfWorkScope, CompositeUnitOfWork,
SuppressedUowScope, and UnitOfWorkManager.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Self-Review (author checklist)

- **Spec coverage:** middleware eager-begin+own (T1 Step 4), aspect join-or-own (T1 Step 7), full Prepare removal incl. accessor skip + PrepareName (T2). Commit-gap closed by the middleware's real commit + aspect ownership rule.
- **Compiles at every task boundary:** T1 leaves the Prepare API present but unused; T2 deletes it with no callers remaining.
- **Type consistency:** middleware uses `_uowManager.Begin(_options.DefaultOptions)`; aspect uses `uowManager.Begin(options)` + `uowManager.Current`; both exist post-change. `Options => _root.Options` matches the removed scope `_options`.
- **No placeholders:** middleware + aspect rewrites are complete code; removal list is exact symbols/files.
- **Breaking (v2-acceptable):** `IUnitOfWork` / `IUnitOfWorkManager` shrink (Prepare members removed); `UnitOfWorkOptions.PrepareName` removed.
