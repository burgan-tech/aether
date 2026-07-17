# Fixed BackgroundJob Schema Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bind every BackgroundJob persistence and lease operation to the application-configured schema while preserving tenant information in job payloads and transaction atomicity with business writes.

**Architecture:** `EfCoreJobStore`, `EfCoreJobArmingLeaseStore`, and `NpgsqlJobArmingLeaseStore` own a nested configured-schema scope for the complete database operation. Standard DI injects the singleton `BackgroundJobOptions` and scoped `ICurrentSchema`; legacy constructors retain ambient-schema behavior. The root Unit of Work still owns one shared connection/transaction and caches the dedicated job context by `(DbContextType, configured schema)`.

**Tech Stack:** .NET 10, C#, Entity Framework Core 10, Npgsql/PostgreSQL, xUnit, Shouldly, NSubstitute, Testcontainers.

## Global Constraints

- `BackgroundJobOptions.Schema` is immutable application configuration during runtime.
- Caller-owned `ICurrentSchema.Change(...)` scopes select business schemas and must be restored before a BackgroundJob store call returns.
- Job rows use the configured BackgroundJob schema; serialized envelopes retain the tenant schema active at enqueue time.
- Null or blank `BackgroundJobOptions.Schema` preserves tenant-local behavior.
- Existing public constructor signatures remain source- and binary-compatible.
- Migration generation remains driven by the dedicated DbContext model; runtime scoping must not rewrite migrations.
- Business and BackgroundJob writes enlisted in one transactional root Unit of Work remain atomic.
- External scheduler calls and later handler execution are outside the enqueue transaction.

---

### Task 1: Bind `EfCoreJobStore` to the configured schema

**Files:**
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/BackgroundJob/EfCoreJobStore.cs`
- Create: `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/BackgroundJob/FixedBackgroundJobSchemaStoreTests.cs`

**Interfaces:**
- Consumes: `BackgroundJobOptions.Schema`, `ICurrentSchema.Change(string)`, `IAetherDbContextProvider<TDbContext>.GetDbContextAsync(CancellationToken)`.
- Produces: every `IJobStore` operation executes under the configured schema and restores the caller schema; the existing one-argument constructor remains available.

- [ ] **Step 1: Write failing store scope tests**

Create an in-memory `JobDbContext` implementing `IHasEfCoreBackgroundJobs`. Use `StaticCurrentSchema("tenant_a")` and a substituted provider whose callback asserts that `currentSchema.Name == "sys_queues"`. Cover a query operation and a staged write:

```csharp
[Fact]
public async Task Job_store_uses_configured_schema_and_restores_tenant()
{
    await using var db = CreateContext();
    var currentSchema = new StaticCurrentSchema("tenant_a");
    var provider = Substitute.For<IAetherDbContextProvider<JobDbContext>>();
    provider.GetDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ =>
    {
        currentSchema.Name.ShouldBe("sys_queues");
        return db;
    });
    var store = new EfCoreJobStore<JobDbContext>(
        provider,
        new BackgroundJobOptions { Schema = "sys_queues" },
        currentSchema);

    (await store.GetAsync(Guid.NewGuid())).ShouldBeNull();
    await store.SaveAsync(CreateJob());

    currentSchema.Name.ShouldBe("tenant_a");
    await provider.Received(2).GetDbContextAsync(Arg.Any<CancellationToken>());
}
```

Add a legacy/null-schema test. Construct the store with the existing one-argument constructor and with `Schema = null`; provider callbacks must observe `tenant_a`, proving both paths preserve ambient behavior.

- [ ] **Step 2: Run the tests and verify RED**

Run:

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests/BBT.Aether.Infrastructure.Tests.csproj \
  --filter "FullyQualifiedName~FixedBackgroundJobSchemaStoreTests" -v:q
```

Expected: compilation fails because the three-argument `EfCoreJobStore` constructor does not exist.

- [ ] **Step 3: Add configured-schema dependencies without removing the legacy constructor**

Retain the current constructor and add the DI constructor plus a private scope helper:

```csharp
private readonly IAetherDbContextProvider<TDbContext> _dbContextProvider;
private readonly BackgroundJobOptions? _options;
private readonly ICurrentSchema? _currentSchema;

public EfCoreJobStore(IAetherDbContextProvider<TDbContext> dbContextProvider)
{
    _dbContextProvider = dbContextProvider
        ?? throw new ArgumentNullException(nameof(dbContextProvider));
}

public EfCoreJobStore(
    IAetherDbContextProvider<TDbContext> dbContextProvider,
    BackgroundJobOptions options,
    ICurrentSchema currentSchema)
    : this(dbContextProvider)
{
    _options = options ?? throw new ArgumentNullException(nameof(options));
    _currentSchema = currentSchema ?? throw new ArgumentNullException(nameof(currentSchema));
}

private IDisposable BeginConfiguredSchemaScope()
{
    return _currentSchema is null || string.IsNullOrWhiteSpace(_options?.Schema)
        ? global::BBT.Aether.NullDisposable.Instance
        : _currentSchema.Change(_options.Schema);
}
```

Add `using BBT.Aether.MultiSchema;`. At the start of every public `IJobStore` method, after argument validation and before resolving a context, add:

```csharp
using var schemaScope = BeginConfiguredSchemaScope();
```

This covers `SaveAsync`, every read method, tracked update, `ExecuteUpdateAsync` CAS operation, and both reaper methods. Keeping the scope alive until the database command completes is required by the QualifiedNames mismatch guard.

- [ ] **Step 4: Run focused and existing job-store tests**

Run:

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests/BBT.Aether.Infrastructure.Tests.csproj \
  --filter "FullyQualifiedName~EfCoreJobStoreTests|FullyQualifiedName~FixedBackgroundJobSchemaStoreTests" -v:q
```

Expected: all filtered tests pass, including direct uses of the legacy constructor.

- [ ] **Step 5: Commit Task 1**

```bash
git add framework/src/BBT.Aether.Infrastructure/BBT/Aether/BackgroundJob/EfCoreJobStore.cs \
  framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/BackgroundJob/FixedBackgroundJobSchemaStoreTests.cs
git commit -m "fix(background-jobs): bind job store to configured schema"
```

---

### Task 2: Bind EF Core and Npgsql arming lease stores

**Files:**
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/BackgroundJob/EfCoreJobArmingLeaseStore.cs`
- Modify: `framework/src/BBT.Aether.Npgsql/BBT/Aether/BackgroundJob/NpgsqlJobArmingLeaseStore.cs`
- Modify: `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/BackgroundJob/FixedBackgroundJobSchemaStoreTests.cs`
- Test: `framework/test/BBT.Aether.Postgres.Tests/BackgroundJob/JobStoreArmingLeaseTests.cs`

**Interfaces:**
- Consumes: configured `BackgroundJobOptions.Schema` and scoped `ICurrentSchema`.
- Produces: `IJobArmingLeaseStore.ClaimBatchAsync` targets the fixed schema for EF and PostgreSQL raw SQL; existing constructors remain available.

- [ ] **Step 1: Write the failing EF lease test**

Extend `FixedBackgroundJobSchemaStoreTests`:

```csharp
[Fact]
public async Task Ef_lease_store_uses_configured_schema_and_restores_tenant()
{
    await using var db = CreateContext();
    var currentSchema = new StaticCurrentSchema("tenant_a");
    var provider = Substitute.For<IAetherDbContextProvider<JobDbContext>>();
    provider.GetDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ =>
    {
        currentSchema.Name.ShouldBe("sys_queues");
        return db;
    });
    var clock = Substitute.For<IClock>();
    clock.UtcNow.Returns(DateTime.UtcNow);
    var store = new EfCoreJobArmingLeaseStore<JobDbContext>(
        provider, clock,
        new BackgroundJobOptions { Schema = "sys_queues" }, currentSchema);

    (await store.ClaimBatchAsync(10, "worker", TimeSpan.FromSeconds(30)))
        .ShouldBeEmpty();
    currentSchema.Name.ShouldBe("tenant_a");
}
```

Also instantiate the existing two-argument constructor and assert it observes `tenant_a`.

- [ ] **Step 2: Run the test and verify RED**

Run the focused Infrastructure command from Task 1.

Expected: compilation fails because the four-argument lease-store constructor does not exist.

- [ ] **Step 3: Implement EF lease scoping with compatibility**

Change the DI constructor to receive `BackgroundJobOptions` and `ICurrentSchema?`, and add the exact old constructor:

```csharp
public EfCoreJobArmingLeaseStore(
    IAetherDbContextProvider<TDbContext> dbContextProvider,
    IClock clock)
    : this(dbContextProvider, clock, new BackgroundJobOptions { Schema = null }, null)
{
}
```

Add `BeginConfiguredSchemaScope()` with the same null/blank fallback as Task 1. Enter it before context resolution and retain it through all candidate reads and conditional updates.

- [ ] **Step 4: Write a failing QualifiedNames Npgsql lease assertion**

In `JobStoreArmingLeaseTests`, add a caller schema `tenant_a` and configure `BackgroundJobOptions.Schema = queueSchema`. Resolve `NpgsqlJobArmingLeaseStore<TestJobDbContext>` through DI, call `ClaimBatchAsync`, and assert the claim came from `queueSchema` while `tenant_a.BackgroundJobs` does not exist.

Expected before implementation: QualifiedNames reports a context/current-schema mismatch or the raw SQL targets the tenant relation.

- [ ] **Step 5: Implement Npgsql lease scoping with compatibility**

Add `BackgroundJobOptions? options` to the DI constructor and preserve the old constructor:

```csharp
public NpgsqlJobArmingLeaseStore(
    IAetherDbContextProvider<TDbContext> dbContextProvider,
    ICurrentSchema currentSchema,
    IClock clock)
    : this(dbContextProvider, currentSchema, clock, null)
{
}
```

At the start of `ClaimBatchAsync`, enter `options.Schema` when configured. Resolve the context and compute `PostgreSqlRelationName.For(entityType, currentSchema.Name)` inside that scope so the UOW key and raw relation both use the fixed schema.

- [ ] **Step 6: Run lease tests and verify GREEN**

Run:

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests/BBT.Aether.Infrastructure.Tests.csproj \
  --filter "FullyQualifiedName~FixedBackgroundJobSchemaStoreTests" -v:q
dotnet test framework/test/BBT.Aether.Postgres.Tests/BBT.Aether.Postgres.Tests.csproj \
  --filter "FullyQualifiedName~JobStoreArmingLeaseTests" -m:1 -v:q
```

Expected: both commands pass; fixed-schema and legacy behavior are covered.

- [ ] **Step 7: Commit Task 2**

```bash
git add framework/src/BBT.Aether.Infrastructure/BBT/Aether/BackgroundJob/EfCoreJobArmingLeaseStore.cs \
  framework/src/BBT.Aether.Npgsql/BBT/Aether/BackgroundJob/NpgsqlJobArmingLeaseStore.cs \
  framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/BackgroundJob/FixedBackgroundJobSchemaStoreTests.cs \
  framework/test/BBT.Aether.Postgres.Tests/BackgroundJob/JobStoreArmingLeaseTests.cs
git commit -m "fix(background-jobs): bind arming leases to configured schema"
```

---

### Task 3: Verify cross-context atomicity, tenant payload, and documentation

**Files:**
- Create: `framework/test/BBT.Aether.Postgres.Tests/BackgroundJob/FixedBackgroundJobSchemaTests.cs`
- Modify: `framework/docs/background-jobs/README.md`

**Interfaces:**
- Consumes: fixed-schema store/lease behavior, `IBackgroundJobService.EnqueueAsync`, and the shared-transaction Unit of Work.
- Produces: end-to-end proof that business/job contexts do not mix schemas, enqueue remains atomic, payload retains its tenant, and documentation states the invariant.

- [ ] **Step 1: Write the PostgreSQL QualifiedNames integration test**

Create separate nested `BusinessDbContext` and `JobDbContext`. Register both with `SchemaSwitchingMode.QualifiedNames`, register BackgroundJob with a unique queue schema, and provide a no-op `IJobScheduler`. Arrange the tenant table and generate job tables into the queue schema from the job context model.

Exercise:

```csharp
using (currentSchema.Change(tenantSchema))
{
    await using var uow = uowManager.Begin(new UnitOfWorkOptions
    {
        Scope = UnitOfWorkScopeOption.RequiresNew,
        IsTransactional = true
    });
    (await businessProvider.GetDbContextAsync()).Entities.Add(
        new BusinessEntity(Guid.NewGuid(), "tenant-data"));
    jobId = await backgroundJobs.EnqueueAsync(
        "TestHandler", "fixed-schema-job", new { Value = 42 }, "@daily");
    await uow.CommitAsync();
}
```

Assert business data exists only in `tenantSchema`, `BackgroundJobs` exists only in `queueSchema`, and no tenant job relation exists. Under a fresh tenant scope/UOW, call `jobStore.GetAsync(jobId)`, deserialize its payload, and assert `envelope.Schema == tenantSchema`. Call `IJobArmingLeaseStore.ClaimBatchAsync` and assert it claims the queue row without a QualifiedNames mismatch. Assert `currentSchema.Name == tenantSchema` after every operation.

- [ ] **Step 2: Add rollback coverage**

Begin another transactional UOW under a new tenant schema, add a business row, enqueue a job, and call `RollbackAsync`. Assert both business and queue row counts remain unchanged. This proves the dedicated job context enlists in the root transaction.

- [ ] **Step 3: Run the integration test and verify GREEN**

Run:

```bash
dotnet test framework/test/BBT.Aether.Postgres.Tests/BBT.Aether.Postgres.Tests.csproj \
  --filter "FullyQualifiedName~FixedBackgroundJobSchemaTests" -m:1 -v:q
```

Expected: enqueue/rollback, payload tenant, job reads, and Npgsql lease assertions pass.

- [ ] **Step 4: Update BackgroundJob documentation**

In `framework/docs/background-jobs/README.md`, use:

```csharp
o.Schema = "sys_queues"; // fixed persistence schema for the dedicated BackgroundJob DbContext
```

Replace “Per-schema arming” with “Fixed persistence schema”. State that one application registration owns one immutable persistence schema, request-time tenant changes never redirect `BackgroundJobs`, payload independently carries the enqueue-time tenant, enqueue atomicity applies only to the shared transactional UOW, later scheduler/handler/outcome work is separate, and migrations use the dedicated context model/default schema.

- [ ] **Step 5: Run fresh full verification**

Run:

```bash
git diff --check
dotnet build framework/BBT.Aether.slnx --no-restore -v:q
dotnet test framework/test/BBT.Aether.Infrastructure.Tests/BBT.Aether.Infrastructure.Tests.csproj \
  --no-build --no-restore -v:q
dotnet test framework/test/BBT.Aether.Postgres.Tests/BBT.Aether.Postgres.Tests.csproj \
  --no-build --no-restore -m:1 -v:q
```

Expected: diff check succeeds, build exits 0, and both test projects report zero failures. Existing package/analyzer warnings may remain; these files introduce no new warnings.

- [ ] **Step 6: Request focused code review**

Review the complete diff against `framework/docs/superpowers/specs/2026-07-18-fixed-infrastructure-schema-design.md`. Check constructor compatibility, complete `IJobStore` coverage, QualifiedNames scope lifetime, payload tenant preservation, Npgsql raw relation selection, and transaction atomicity.

- [ ] **Step 7: Commit Task 3**

```bash
git add framework/test/BBT.Aether.Postgres.Tests/BackgroundJob/FixedBackgroundJobSchemaTests.cs \
  framework/docs/background-jobs/README.md
git commit -m "test(background-jobs): verify fixed schema isolation"
```
