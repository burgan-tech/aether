# Qualified Names and Unit of Work Event Semantics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement runtime PostgreSQL qualified-name schema switching, explicit raw-SQL schema tokens, schema-safe framework raw SQL, and commit-only non-transactional domain-event dispatch with hardened nested UOW semantics.

**Architecture:** QualifiedNames gives schema-agnostic EF mappings a constant model placeholder and replaces it with the immutable schema bound to each DbContext at command execution. The UOW caches contexts by `(DbContextType, Schema)`, buffers events together with their producing schema, and dispatches only from the owning root commit. Framework-owned ADO.NET lease queries always use validated fully qualified relation names.

**Tech Stack:** .NET 10, C# latest, EF Core 10, Npgsql, xUnit, Shouldly, Testcontainers PostgreSQL.

## Global Constraints

- Runtime schema comes from `ICurrentSchema.Name` when the repository/provider operation resolves its DbContext.
- One repository instance supports `tenant_a -> tenant_b -> tenant_a` within one request and UOW.
- DbContext, DbSet, and IQueryable instances remain bound to their resolution schema.
- QualifiedNames issues no `SET`, `SET LOCAL`, or `RESET search_path` commands.
- Schema-dependent `FromSqlRaw` and `ExecuteSqlRaw` use the exact `{{schema}}` token.
- Do not parse or regex-rewrite arbitrary PostgreSQL relation syntax.
- Non-transactional business and outbox writes are not atomic.
- Preserve `TransactionLocal`, `SessionSearchPath`, SQL Server, and `RequiresNew` behavior.
- Add no NuGet dependency.

---

### Task 1: QualifiedNames model marker and command rewriting

**Files:**
- Create: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/EntityFrameworkCore/AetherSchemaModel.cs`
- Create: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/EntityFrameworkCore/AetherSchemaModelOptionsExtension.cs`
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Domain/EntityFrameworkCore/AetherDbContext.cs`
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/EntityFrameworkCore/IAetherDatabaseProvider.cs`
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/EntityFrameworkCore/AetherDbContextConfigurator.cs`
- Modify: `framework/src/BBT.Aether.Npgsql/NpgsqlAetherProvider.cs`
- Modify: `framework/src/BBT.Aether.Npgsql/SearchPathCommandInterceptor.cs`
- Modify: `framework/src/BBT.Aether.SqlServer/SqlServerAetherProvider.cs`
- Test: `framework/test/BBT.Aether.Postgres.Tests/QualifiedNamesTests.cs`
- Modify: `framework/test/BBT.Aether.Postgres.Tests/DbContextConfiguratorTests.cs`
- Modify: `framework/test/BBT.Aether.Postgres.Tests/MultiSchemaUnitOfWorkTests.cs`
- Modify: `framework/test/BBT.Aether.Postgres.Tests/PgBouncerSearchPathTests.cs`
- Modify: `framework/test/BBT.Aether.Postgres.Tests/UnitOfWorkMiddlewareTests.cs`

**Interfaces:**
- Produces: `AetherSchemaModel.Placeholder`, `AetherSchemaModel.RawSqlToken`, and `UseAetherQualifiedNamesModel()`.
- Changes: `IAetherDatabaseProvider.ApplyShared(...)` receives `ICurrentSchema`.
- Preserves: one EF model for all tenant schemas in QualifiedNames mode.

- [ ] **Step 1: Write the failing runtime repository-switch test**

Create a PostgreSQL test context with schema-agnostic `ToTable("things")`, register `SchemaSwitchingMode.QualifiedNames`, and use this test body:

Register the repository explicitly in the test provider:

```csharp
services.AddScoped<IEfCoreRepository<Thing, Guid>>(sp =>
    new EfCoreRepository<TestDbContext, Thing, Guid>(
        sp.GetRequiredService<IAetherDbContextProvider<TestDbContext>>(), sp));
```

```csharp
[Fact]
public async Task Same_repository_switches_tenant_a_to_tenant_b_and_back()
{
    await ArrangeSchemasAsync();
    await using var scope = _provider.CreateAsyncScope();
    var sp = scope.ServiceProvider;
    var currentSchema = sp.GetRequiredService<ICurrentSchema>();
    var manager = sp.GetRequiredService<IUnitOfWorkManager>();
    var repository = sp.GetRequiredService<IEfCoreRepository<Thing, Guid>>();

    await using var uow = manager.Begin(new UnitOfWorkOptions
    {
        Scope = UnitOfWorkScopeOption.RequiresNew,
        IsTransactional = false
    });

    using (currentSchema.Change(_schemaA))
        await repository.InsertAsync(new Thing(Guid.NewGuid(), "a"), true);
    using (currentSchema.Change(_schemaB))
        await repository.InsertAsync(new Thing(Guid.NewGuid(), "b"), true);
    using (currentSchema.Change(_schemaA))
        (await repository.GetListAsync()).Select(x => x.Name).ShouldBe(["a"]);
    using (currentSchema.Change(_schemaB))
        (await repository.GetListAsync()).Select(x => x.Name).ShouldBe(["b"]);

    await uow.CommitAsync();
}
```

Capture command text with a test interceptor and assert qualified tables are present and search-path commands are absent. Also obtain an `IQueryable` under schema A, execute it under schema B, and assert the mismatch exception occurs before the command-capture interceptor records database access.

- [ ] **Step 2: Run RED**

Run: `dotnet test framework/test/BBT.Aether.Postgres.Tests/BBT.Aether.Postgres.Tests.csproj --filter FullyQualifiedName~QualifiedNamesTests.Same_repository_switches_tenant_a_to_tenant_b_and_back -m:1`

Expected: FAIL with `SchemaSwitchingMode.QualifiedNames is not yet implemented`.

- [ ] **Step 3: Add the provider-neutral model marker**

Implement:

```csharp
public static class AetherSchemaModel
{
    public const string Placeholder = "__aether_schema__";
    public const string QuotedPlaceholder = "\"__aether_schema__\"";
    public const string RawSqlToken = "{{schema}}";
}
```

`AetherSchemaModelOptionsExtension` implements `IDbContextOptionsExtension`, uses one stable non-zero service-provider hash, and exposes `UseAetherQualifiedNamesModel()`. Store the constructor options in `AetherDbContext<TDbContext>` and add:

```csharp
if (_options.FindExtension<AetherSchemaModelOptionsExtension>() is not null)
    modelBuilder.HasDefaultSchema(AetherSchemaModel.Placeholder);
```

- [ ] **Step 4: Pass current schema through provider configuration**

Change the seam to:

```csharp
void ApplyShared(
    DbContextOptionsBuilder builder,
    DbConnection sharedConnection,
    string schema,
    SchemaScopeState state,
    ICurrentSchema currentSchema);
```

Resolve `ICurrentSchema` from the configurator's existing `IServiceProvider` and pass it to `ApplyShared`. Npgsql uses it; SQL Server accepts and ignores it. Npgsql enables the model extension only for QualifiedNames. Update the four tests that construct `AetherDbContextConfigurator` directly so their service provider contains `ICurrentSchema` rather than passing `null!`.

- [ ] **Step 5: Implement exact-token rewriting and mismatch protection**

In `SearchPathCommandInterceptor` validate the bound schema once, then use:

```csharp
private void ApplyQualifiedNames(DbCommand command)
{
    if (!string.Equals(currentSchema.Name, _schema, StringComparison.Ordinal))
        throw new InvalidOperationException(
            $"DbContext is bound to schema '{_schema}', but current schema is " +
            $"'{currentSchema.Name ?? "<none>"}'. Resolve the DbContext again inside the new schema scope.");

    command.CommandText = command.CommandText
        .Replace(AetherSchemaModel.QuotedPlaceholder, _quotedSchema, StringComparison.Ordinal);
}
```

Call it from sync/async reader, non-query, and scalar paths without executing a second command.

- [ ] **Step 6: Run GREEN**

Run: `dotnet test framework/test/BBT.Aether.Postgres.Tests/BBT.Aether.Postgres.Tests.csproj --filter FullyQualifiedName~QualifiedNamesTests -m:1`

Expected: PASS; SQL contains `"tenant"."things"` and no search-path commands.

- [ ] **Step 7: Commit**

```bash
git add framework/src/BBT.Aether.Infrastructure framework/src/BBT.Aether.Npgsql framework/test/BBT.Aether.Postgres.Tests/QualifiedNamesTests.cs
git commit -m "feat(npgsql): implement runtime qualified schema names"
```

### Task 2: Raw SQL tokens and framework-owned ADO.NET queries

**Files:**
- Modify: `framework/src/BBT.Aether.Npgsql/PostgreSqlIdentifier.cs`
- Create: `framework/src/BBT.Aether.Npgsql/PostgreSqlRelationName.cs`
- Modify: `framework/src/BBT.Aether.Npgsql/BBT/Aether/Events/NpgsqlOutboxLeaseStore.cs`
- Modify: `framework/src/BBT.Aether.Npgsql/BBT/Aether/Events/NpgsqlInboxLeaseStore.cs`
- Modify: `framework/src/BBT.Aether.Npgsql/BBT/Aether/BackgroundJob/NpgsqlJobArmingLeaseStore.cs`
- Modify: `framework/test/BBT.Aether.Postgres.Tests/QualifiedNamesTests.cs`
- Modify: `framework/test/BBT.Aether.Postgres.Tests/NpgsqlLeaseStoreTests.cs`
- Modify: `framework/test/BBT.Aether.Postgres.Tests/PostgreSqlIdentifierTests.cs`

**Interfaces:**
- Produces: `PostgreSqlIdentifier.QuoteTable(...)` and `PostgreSqlRelationName.For(...)`.
- Consumes: Task 1's model placeholder and raw SQL command token.

- [ ] **Step 1: Write failing FromSqlRaw and ExecuteSqlRaw tests**

```csharp
var rows = await db.Set<Thing>()
    .FromSqlRaw("SELECT * FROM {{schema}}.\"things\" WHERE \"Name\" = {0}", "a")
    .ToListAsync();

await db.Database.ExecuteSqlRawAsync(
    "UPDATE {{schema}}.\"things\" SET \"Name\" = {0} WHERE \"Name\" = {1}",
    "updated", "a");
```

Assert tenant isolation, repeated tokens in a join/subquery, preserved DbParameters, successful schema-independent `SELECT 1`, and pre-database rejection of invalid/over-length runtime schemas.

- [ ] **Step 2: Run RED**

Run: `dotnet test framework/test/BBT.Aether.Postgres.Tests/BBT.Aether.Postgres.Tests.csproj --filter "FullyQualifiedName~QualifiedNamesTests&Name~Raw" -m:1`

Expected: FAIL because `{{schema}}` reaches PostgreSQL unchanged before Task 1's raw-token branch is complete.

- [ ] **Step 3: Add shared identifier and relation formatting**

Refactor identifier validation to support schemas and tables:

```csharp
public static string QuoteTable(string table) => Quote(table, nameof(table));
public static string QuoteSchema(string schema) => Quote(schema, nameof(schema));
```

Implement:

```csharp
public static string For(IReadOnlyEntityType entityType, string runtimeSchema)
{
    var table = entityType.GetTableName()
        ?? throw new InvalidOperationException($"Entity '{entityType.Name}' has no table mapping.");
    var mapped = entityType.GetSchema();
    var schema = string.IsNullOrWhiteSpace(mapped) || mapped == AetherSchemaModel.Placeholder
        ? runtimeSchema
        : mapped;
    return $"{PostgreSqlIdentifier.QuoteSchema(schema)}.{PostgreSqlIdentifier.QuoteTable(table)}";
}
```

Extend the QualifiedNames branch from Task 1 with explicit raw-token substitution:

```csharp
command.CommandText = command.CommandText
    .Replace(AetherSchemaModel.QuotedPlaceholder, _quotedSchema, StringComparison.Ordinal)
    .Replace(AetherSchemaModel.RawSqlToken, _quotedSchema, StringComparison.Ordinal);
```

Do not alter any other SQL text. In non-QualifiedNames modes, reject `{{schema}}` with guidance to use QualifiedNames or the mode's documented search-path contract.

- [ ] **Step 4: Fully qualify all three raw lease stores**

Replace metadata formatting and every store-created `SET LOCAL` block with:

```csharp
var schema = currentSchema.Name
    ?? throw new InvalidOperationException("Current schema is not set.");
var fullTableName = PostgreSqlRelationName.For(entityType, schema);
```

Keep values parameterized. This applies in every switching mode because these direct ADO.NET commands bypass EF interceptors.

- [ ] **Step 5: Run GREEN**

Run: `dotnet test framework/test/BBT.Aether.Postgres.Tests/BBT.Aether.Postgres.Tests.csproj --filter "FullyQualifiedName~QualifiedNamesTests|FullyQualifiedName~NpgsqlLeaseStoreTests|FullyQualifiedName~PostgreSqlIdentifierTests|FullyQualifiedName~JobStoreArmingLeaseTests" -m:1`

Expected: PASS; framework raw SQL uses `"schema"."table"` and emits no store-owned `SET LOCAL`.

- [ ] **Step 6: Commit**

```bash
git add framework/src/BBT.Aether.Npgsql framework/test/BBT.Aether.Postgres.Tests
git commit -m "fix(npgsql): qualify framework raw SQL relations"
```

### Task 3: Schema-bound event buffering and commit-only non-transactional dispatch

**Files:**
- Create: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/PendingDomainEvent.cs`
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/CompositeUnitOfWork.cs`
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Domain/EntityFrameworkCore/DomainEventDispatcher.cs`
- Modify: `framework/src/BBT.Aether.Core/BBT/Aether/Events/AetherDomainEventOptions.cs`
- Modify: `framework/test/BBT.Aether.Postgres.Tests/NonTransactionalOutboxDispatchTests.cs`
- Create: `framework/test/BBT.Aether.Postgres.Tests/MultiSchemaDomainEventTests.cs`
- Create: `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Uow/DomainEventFailureTests.cs`

**Interfaces:**
- Produces: internal `PendingDomainEvent(string Schema, DomainEventEnvelope Envelope)`.
- Removes: `AetherDomainEventOptions.DispatchNonTransactionalEventsToOutbox`.
- Changes: outbox failures propagate to `CommitAsync`.

- [ ] **Step 1: Replace option tests with a failing commit-boundary test**

Configure no non-transactional flag and assert both boundaries:

```csharp
await uow.SaveChangesAsync();
(await CountAsync("OutboxMessages")).ShouldBe(0);

await uow.CommitAsync();
(await CountAsync("OutboxMessages")).ShouldBe(1);
```

Rename the test to `NonTransactional_SaveChanges_buffers_and_Commit_writes_outbox` and delete the historical flag-off test.

- [ ] **Step 2: Write the failing multi-schema event-placement test**

In one non-transactional UOW, add an aggregate under schema A and one under schema B, call `SaveChangesAsync`, return ambient schema to A, and commit. Assert one outbox row per schema and deserialize each `CloudEventEnvelope` to assert its `Schema` equals the producing schema.

- [ ] **Step 3: Run RED**

Run: `dotnet test framework/test/BBT.Aether.Postgres.Tests/BBT.Aether.Postgres.Tests.csproj --filter "FullyQualifiedName~NonTransactionalOutboxDispatchTests|FullyQualifiedName~MultiSchemaDomainEventTests" -m:1`

Expected: no-flag dispatch yields zero outbox rows; multi-schema dispatch uses the commit-time ambient schema.

- [ ] **Step 4: Capture schema with every buffered event**

Add:

```csharp
internal sealed record PendingDomainEvent(string Schema, DomainEventEnvelope Envelope);
```

Change `_events` to `List<PendingDomainEvent>`. Construct `BufferEnqueuer` with the schema passed to `GetDbContextAsync`. Resolve `ICurrentSchema` once from `serviceProvider` inside the root UOW and save each context under its bound schema so Task 1's mismatch guard remains valid during root commit:

```csharp
foreach (var (key, context) in _contexts)
{
    if (!context.ChangeTracker.HasChanges()) continue;
    using (currentSchema.Change(key.Schema))
        await context.SaveChangesAsync(cancellationToken);
}
```

- [ ] **Step 5: Dispatch schema groups only from CommitAsync**

Implement:

```csharp
private async Task ForEachEventGroupAsync(
    Func<IReadOnlyList<DomainEventEnvelope>, CancellationToken, Task> action,
    CancellationToken cancellationToken)
{
    foreach (var group in _events.GroupBy(x => x.Schema, StringComparer.Ordinal))
    {
        using (currentSchema.Change(group.Key))
            await action(group.Select(x => x.Envelope).ToList(), cancellationToken);
    }
}
```

Throw when pending events exist without `IDomainEventDispatcher`. Add a `DomainEventFailureTests` case that buffers one event without registering the dispatcher, asserts `CommitAsync` throws, and asserts `IsCompleted == false`. Remove the option check. In non-transactional `AlwaysUseOutbox`, dispatch groups, save outbox rows, then clear. In `PublishWithFallback`, publish/fallback per schema and remove a group only after success. Reuse the same grouping for transactional paths.

- [ ] **Step 6: Stop swallowing dispatcher failures**

Remove per-envelope catches from `DomainEventDispatcher.DispatchEventsAsync`; log and let `eventBus.PublishAsync(...)` throw. Materialize the event list once in `WriteToOutboxInNewScopeAsync` before publishing and counting. Delete `DispatchNonTransactionalEventsToOutbox` from options.

- [ ] **Step 7: Run GREEN**

Run:

```bash
dotnet test framework/test/BBT.Aether.Postgres.Tests/BBT.Aether.Postgres.Tests.csproj --filter "FullyQualifiedName~NonTransactionalOutboxDispatchTests|FullyQualifiedName~MultiSchemaDomainEventTests|FullyQualifiedName~OutboxWithinSharedTransactionTests" -m:1
dotnet test framework/test/BBT.Aether.Infrastructure.Tests/BBT.Aether.Infrastructure.Tests.csproj --filter FullyQualifiedName~DomainEventFailureTests -m:1
```

Expected: PASS; failure test sees the original exception and `uow.IsCompleted` remains false.

- [ ] **Step 8: Commit**

```bash
git add framework/src/BBT.Aether.Core framework/src/BBT.Aether.Infrastructure framework/test/BBT.Aether.Postgres.Tests framework/test/BBT.Aether.Infrastructure.Tests
git commit -m "fix(uow): dispatch schema-bound events only at commit"
```

### Task 4: Nested Required scope ownership and compatibility

**Files:**
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/UnitOfWorkScope.cs`
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/UnitOfWorkManager.cs`
- Modify: `framework/src/BBT.Aether.Core/BBT/Aether/Uow/UnitOfWorkOptions.cs`
- Create: `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Uow/NestedUnitOfWorkTests.cs`
- Modify: `framework/test/BBT.Aether.Postgres.Tests/UnitOfWorkDisposalTests.cs`

**Interfaces:**
- Preserves: owner and `RequiresNew` physical completion.
- Changes: participating `Required` commit is logical-only; participating rollback aborts the root.

- [ ] **Step 1: Write failing nested ownership tests**

The core test is:

```csharp
[Fact]
public async Task Inner_required_commit_does_not_complete_root()
{
    var manager = _scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
    await using var outer = manager.Begin(new UnitOfWorkOptions
    {
        Scope = UnitOfWorkScopeOption.RequiresNew,
        IsTransactional = false
    });
    await using var inner = manager.Begin(new UnitOfWorkOptions
    {
        Scope = UnitOfWorkScopeOption.Required,
        IsTransactional = false
    });

    await inner.CommitAsync();
    outer.IsCompleted.ShouldBeFalse();
    await outer.CommitAsync();
    outer.IsCompleted.ShouldBeTrue();
}
```

Add tests for inner rollback aborting outer and transactional inner Required over non-transactional outer throwing with `RequiresNew` guidance.

- [ ] **Step 2: Run RED**

Run: `dotnet test framework/test/BBT.Aether.Infrastructure.Tests/BBT.Aether.Infrastructure.Tests.csproj --filter FullyQualifiedName~NestedUnitOfWorkTests -m:1`

Expected: inner commit currently completes the root; incompatible Required currently joins silently.

- [ ] **Step 3: Gate physical completion by ownership**

Implement participant completion state:

```csharp
public async Task CommitAsync(CancellationToken cancellationToken = default)
{
    if (_ownsRoot) await _root.CommitAsync(cancellationToken);
    else _participantCompleted = true;
}

public async Task RollbackAsync(CancellationToken cancellationToken = default)
{
    if (_ownsRoot) await _root.RollbackAsync(cancellationToken);
    else
    {
        _root.Abort();
        _participantCompleted = true;
    }
}
```

Participant `IsCompleted` returns `_participantCompleted`; owner `IsCompleted` returns root completion. Participant disposal does not auto-abort, preserving existing service participation patterns.

- [ ] **Step 4: Reject impossible Required escalation**

Before returning a participant in `Begin` and `BeginAsync`:

```csharp
if (options.IsTransactional && existing.Root.Options?.IsTransactional != true)
    throw new InvalidOperationException(
        "A transactional Required UnitOfWork cannot join a non-transactional outer UnitOfWork. " +
        "Use UnitOfWorkScopeOption.RequiresNew.");
```

Update `IsTransactional` XML docs to state that root transaction mode cannot be escalated later.

- [ ] **Step 5: Run GREEN**

Run:

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests/BBT.Aether.Infrastructure.Tests.csproj --filter "FullyQualifiedName~NestedUnitOfWorkTests|FullyQualifiedName~AmbientBeginTests" -m:1
dotnet test framework/test/BBT.Aether.Postgres.Tests/BBT.Aether.Postgres.Tests.csproj --filter FullyQualifiedName~UnitOfWorkDisposalTests -m:1
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add framework/src/BBT.Aether.Core/BBT/Aether/Uow/UnitOfWorkOptions.cs framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Uow framework/test/BBT.Aether.Postgres.Tests/UnitOfWorkDisposalTests.cs
git commit -m "fix(uow): preserve root ownership in nested scopes"
```

### Task 5: Documentation, compatibility sweep, and full verification

**Files:**
- Modify: `framework/docs/multi-schema/README.md`
- Modify: `framework/docs/multi-schema/IMPLEMENTATION_NOTES.md`
- Modify: `framework/docs/multi-schema/ADOPTION-GUIDE.md`
- Modify: `framework/docs/unit-of-work/README.md`
- Modify: `framework/docs/domain-events/README.md`
- Modify: `framework/docs/superpowers/specs/2026-07-14-qualified-names-and-uow-events-design.md`

**Interfaces:**
- Documents: QualifiedNames registration, `{{schema}}`, runtime repository switching, schema-bound query lifetimes, commit-only events, and non-transactional atomicity limits.

- [ ] **Step 1: Replace QualifiedNames stub documentation**

Include:

```csharp
services.AddAetherNpgsql<AppDbContext>(
    connectionString,
    SchemaSwitchingMode.QualifiedNames);

using (currentSchema.Change("tenant_b"))
{
    var rows = await repository.GetListAsync();
    await db.Database.ExecuteSqlRawAsync(
        "UPDATE {{schema}}.\"orders\" SET \"Status\" = {0}",
        status);
}
```

State that repository/service instances may be reused but resolved DbContext, DbSet, and IQueryable objects may not cross schema scopes.

- [ ] **Step 2: Document UOW timing and nested ownership**

Add:

```text
Non-transactional SaveChanges -> business write plus schema-bound event buffer
Non-transactional Commit      -> schema-grouped outbox or direct dispatch
```

Remove the deleted flag. State that non-transactional business/outbox writes are not atomic, only the owning root physically commits, and transactional inner Required needs RequiresNew over a non-transactional outer.

- [ ] **Step 3: Run stale-text and diff checks**

Run:

```bash
rg -n "QualifiedNames.*not yet implemented|DispatchNonTransactionalEventsToOutbox|transaction can be escalated" framework/src framework/test framework/docs/multi-schema framework/docs/unit-of-work framework/docs/domain-events
git diff --check
```

Expected: `rg` finds no stale references and `git diff --check` exits 0.

- [ ] **Step 4: Run complete test projects**

Run:

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests/BBT.Aether.Infrastructure.Tests.csproj --no-restore -m:1
dotnet test framework/test/BBT.Aether.Postgres.Tests/BBT.Aether.Postgres.Tests.csproj --no-restore -m:1
```

Expected: both projects report zero failures.

- [ ] **Step 5: Build the framework solution**

Run: `dotnet build framework/BBT.Aether.slnx --no-restore -m:1`

Expected: `Build succeeded` with zero errors.

- [ ] **Step 6: Review scope and requirements**

Run:

```bash
git status --short
git diff --stat
git diff -- framework/src/BBT.Aether.Core framework/src/BBT.Aether.Infrastructure framework/src/BBT.Aether.Npgsql framework/test framework/docs
```

Confirm every design requirement has a test and the untracked repository-root `AGENTS.md` is not staged.

- [ ] **Step 7: Commit**

```bash
git add framework/docs framework/src framework/test
git commit -m "docs: describe qualified schema and commit event semantics"
```
