# Non-Transactional UnitOfWork — Schema Switching Modes

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `IsTransactional = false` actually skip the database transaction, with schema isolation behavior that is **explicitly configured** per deployment (not auto-detected), covering three real-world scenarios: transactional, non-transactional with direct pooling, and non-transactional with PgBouncer transaction pooling.

**Architecture:** Introduce `SchemaSwitchingMode` enum in the Npgsql package. `NpgsqlAetherProvider` accepts the mode at construction and wires the correct `SearchPathCommandInterceptor` behavior. Two of the three modes (`TransactionLocal`, `SessionSearchPath`) are fully implemented; `QualifiedNames` is stubbed with a clear error. Schema cleanup for `SessionSearchPath` is carried on `SchemaScopeState.Cleanup` — set by the provider, invoked by `CompositeUnitOfWork.DisposeAsync` before the connection is returned to the pool.

**Tech Stack:** .NET 10, EF Core interceptors, Npgsql, xUnit, Shouldly

---

## Mode semantics

| Mode | How it works | When to use |
|---|---|---|
| `TransactionLocal` | `SET LOCAL search_path` — transaction-scoped, auto-reverts | Transactional UoW, any pool |
| `SessionSearchPath` | `SET search_path` once + `RESET` at UoW dispose | Non-transactional + direct/session pooling (Npgsql native pool) |
| `QualifiedNames` | Schema-qualified SQL rewrite — no search_path | Non-transactional + PgBouncer transaction pooling (not yet implemented) |

---

## Files

| Action | File |
|---|---|
| Create | `framework/src/BBT.Aether.Npgsql/BBT/Aether/Uow/EntityFrameworkCore/SchemaSwitchingMode.cs` |
| Modify | `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/EntityFrameworkCore/SchemaScopeState.cs` |
| Modify | `framework/src/BBT.Aether.Npgsql/SearchPathCommandInterceptor.cs` |
| Modify | `framework/src/BBT.Aether.Npgsql/NpgsqlAetherProvider.cs` |
| Modify | `framework/src/BBT.Aether.Npgsql/Microsoft/Extensions/DependencyInjection/AetherNpgsqlServiceCollectionExtensions.cs` |
| Modify | `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/CompositeUnitOfWork.cs` |
| Modify | `framework/test/BBT.Aether.Postgres.Tests/UnitOfWorkDisposalTests.cs` |

---

### Task 1: `SchemaSwitchingMode` enum

**Files:**
- Create: `framework/src/BBT.Aether.Npgsql/BBT/Aether/Uow/EntityFrameworkCore/SchemaSwitchingMode.cs`

- [ ] **Step 1: Create the enum**

```csharp
namespace BBT.Aether.Uow.EntityFrameworkCore;

/// <summary>
/// Controls how a schema-bound Npgsql DbContext switches the active PostgreSQL search_path.
/// Choose based on your connection pool topology.
/// </summary>
public enum SchemaSwitchingMode
{
    /// <summary>
    /// Issues <c>SET LOCAL search_path</c> before each command.
    /// The effect is automatically reverted at transaction end by PostgreSQL.
    /// <para>Requires <c>IsTransactional = true</c>. Works with any connection pool.</para>
    /// </summary>
    TransactionLocal,

    /// <summary>
    /// Issues a session-level <c>SET search_path</c> before the first command to a given schema,
    /// then <c>RESET search_path</c> when the UnitOfWork is disposed (before the connection
    /// is returned to the pool).
    /// <para>
    /// Use with <c>IsTransactional = false</c> and Npgsql's native connection pool (direct or
    /// session pooling). NOT safe with PgBouncer transaction pooling because the session-level
    /// <c>SET</c> may not survive across PgBouncer backend switches.
    /// </para>
    /// </summary>
    SessionSearchPath,

    /// <summary>
    /// Rewrites SQL to use fully-qualified <c>"schema"."table"</c> names. No <c>search_path</c>
    /// manipulation is performed.
    /// <para>
    /// Intended for <c>IsTransactional = false</c> behind PgBouncer transaction pooling.
    /// </para>
    /// <para><b>Not yet implemented.</b> Throws <see cref="NotSupportedException"/>.</para>
    /// </summary>
    QualifiedNames,
}
```

- [ ] **Step 2: Build**

```bash
dotnet build framework/src/BBT.Aether.Npgsql 2>&1 | tail -5
```

Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add framework/src/BBT.Aether.Npgsql/BBT/Aether/Uow/EntityFrameworkCore/SchemaSwitchingMode.cs
git commit -m "feat(npgsql): add SchemaSwitchingMode enum"
```

---

### Task 2: `SchemaScopeState` — cleanup delegate

**Files:**
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/EntityFrameworkCore/SchemaScopeState.cs`

**Context:** `SessionSearchPath` mode needs to run `RESET search_path` once when the UoW disposes — after all queries, before the connection returns to the pool. Rather than coupling `CompositeUnitOfWork` to Postgres SQL, the provider registers a cleanup delegate here when it wires up `SessionSearchPath`. `TransactionLocal` never sets it.

- [ ] **Step 1: Add the Cleanup property**

Replace the entire file:

```csharp
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Uow.EntityFrameworkCore;

/// <summary>
/// Tracks the schema most recently applied on a UnitOfWork's single shared connection,
/// letting a provider's schema interceptor skip a redundant re-apply when consecutive
/// commands target the same schema. Not thread-safe by design: commands on a single
/// connection are serialized and one instance is scoped to one UnitOfWork.
/// </summary>
public sealed class SchemaScopeState
{
    /// <summary>The schema name most recently written to the connection's search context.</summary>
    public string? Current { get; set; }

    /// <summary>
    /// Optional cleanup to run just before the shared connection is disposed (i.e. returned to
    /// the pool). Set by providers that use session-level state (e.g. <c>SET search_path</c>) so
    /// that the state is always reset before the next caller borrows the connection.
    /// <para>Only invoked when <see cref="Current"/> is non-null (meaning the state was actually
    /// written at least once during this UnitOfWork).</para>
    /// </summary>
    public Func<DbConnection, CancellationToken, Task>? Cleanup { get; set; }
}
```

- [ ] **Step 2: Build Infrastructure**

```bash
dotnet build framework/src/BBT.Aether.Infrastructure 2>&1 | tail -5
```

Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/EntityFrameworkCore/SchemaScopeState.cs
git commit -m "feat(uow): add SchemaScopeState.Cleanup for session-level schema reset"
```

---

### Task 3: `SearchPathCommandInterceptor` — mode-aware

**Files:**
- Modify: `framework/src/BBT.Aether.Npgsql/SearchPathCommandInterceptor.cs`

**Context:** The interceptor gets `SchemaSwitchingMode` at construction. Behavior per mode:
- `TransactionLocal`: throws if no transaction (unchanged guard); `SET LOCAL`.
- `SessionSearchPath`: no transaction required; `SET search_path` (session-level); SchemaScopeState optimization applies (skip if already current). No RESET here — that is handled at dispose via `SchemaScopeState.Cleanup`.
- `QualifiedNames`: throws `NotSupportedException`.

- [ ] **Step 1: Replace the file**

```csharp
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BBT.Aether.Uow.EntityFrameworkCore;

/// <summary>
/// Sets the active PostgreSQL <c>search_path</c> before each command issued by a schema-bound
/// <see cref="Microsoft.EntityFrameworkCore.DbContext"/>. Behaviour depends on
/// <see cref="SchemaSwitchingMode"/>:
/// <list type="bullet">
///   <item>
///     <term><see cref="SchemaSwitchingMode.TransactionLocal"/></term>
///     <description>
///       Issues <c>SET LOCAL search_path</c> inside the active transaction.
///       PostgreSQL reverts the effect at transaction end automatically.
///       Throws if the command has no transaction.
///     </description>
///   </item>
///   <item>
///     <term><see cref="SchemaSwitchingMode.SessionSearchPath"/></term>
///     <description>
///       Issues a session-level <c>SET search_path</c> when the schema changes.
///       The caller (UnitOfWork dispose) is responsible for running <c>RESET search_path</c>
///       before returning the connection to the pool via <see cref="SchemaScopeState.Cleanup"/>.
///     </description>
///   </item>
///   <item>
///     <term><see cref="SchemaSwitchingMode.QualifiedNames"/></term>
///     <description>Not yet implemented — throws <see cref="NotSupportedException"/>.</description>
///   </item>
/// </list>
/// <remarks>
/// Assumes query results are buffered (EF Core's default). A single Npgsql connection does not
/// support multiple active result sets; do not stream (<c>AsAsyncEnumerable</c> without
/// materializing) across interleaved schema-bound contexts on the same connection.
/// </remarks>
/// </summary>
public sealed class SearchPathCommandInterceptor(
    string schema,
    SchemaScopeState state,
    SchemaSwitchingMode mode) : DbCommandInterceptor
{
    private readonly string _schema = schema;
    private readonly string _setLocal =
        $"SET LOCAL search_path TO {PostgreSqlIdentifier.QuoteSchema(schema)}, public";
    private readonly string _setSession =
        $"SET search_path TO {PostgreSqlIdentifier.QuoteSchema(schema)}, public";

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        ApplySearchPath(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await ApplySearchPathAsync(command, cancellationToken);
        return result;
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        ApplySearchPath(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await ApplySearchPathAsync(command, cancellationToken);
        return result;
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        ApplySearchPath(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        await ApplySearchPathAsync(command, cancellationToken);
        return result;
    }

    private void ApplySearchPath(DbCommand command)
    {
        switch (mode)
        {
            case SchemaSwitchingMode.TransactionLocal:
                if (command.Transaction is null)
                {
                    throw new InvalidOperationException(
                        $"SchemaSwitchingMode.TransactionLocal requires a transaction, but none is active. " +
                        $"Use IsTransactional = true, or switch to SchemaSwitchingMode.SessionSearchPath " +
                        $"(direct/session pool) or SchemaSwitchingMode.QualifiedNames (PgBouncer transaction pool).");
                }
                if (state.Current == _schema) return;
                using (var cmd = command.Connection!.CreateCommand())
                {
                    cmd.Transaction = command.Transaction;
                    cmd.CommandText = _setLocal;
                    cmd.ExecuteNonQuery();
                }
                state.Current = _schema;
                break;

            case SchemaSwitchingMode.SessionSearchPath:
                if (state.Current == _schema) return;
                using (var cmd = command.Connection!.CreateCommand())
                {
                    cmd.CommandText = _setSession;
                    cmd.ExecuteNonQuery();
                }
                state.Current = _schema;
                break;

            case SchemaSwitchingMode.QualifiedNames:
                throw new NotSupportedException(
                    "SchemaSwitchingMode.QualifiedNames is not yet implemented.");

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown SchemaSwitchingMode.");
        }
    }

    private async Task ApplySearchPathAsync(DbCommand command, CancellationToken cancellationToken)
    {
        switch (mode)
        {
            case SchemaSwitchingMode.TransactionLocal:
                if (command.Transaction is null)
                {
                    throw new InvalidOperationException(
                        $"SchemaSwitchingMode.TransactionLocal requires a transaction, but none is active. " +
                        $"Use IsTransactional = true, or switch to SchemaSwitchingMode.SessionSearchPath " +
                        $"(direct/session pool) or SchemaSwitchingMode.QualifiedNames (PgBouncer transaction pool).");
                }
                if (state.Current == _schema) return;
                await using (var cmd = command.Connection!.CreateCommand())
                {
                    cmd.Transaction = command.Transaction;
                    cmd.CommandText = _setLocal;
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }
                state.Current = _schema;
                break;

            case SchemaSwitchingMode.SessionSearchPath:
                if (state.Current == _schema) return;
                await using (var cmd = command.Connection!.CreateCommand())
                {
                    cmd.CommandText = _setSession;
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }
                state.Current = _schema;
                break;

            case SchemaSwitchingMode.QualifiedNames:
                throw new NotSupportedException(
                    "SchemaSwitchingMode.QualifiedNames is not yet implemented.");

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown SchemaSwitchingMode.");
        }
    }
}
```

- [ ] **Step 2: Build**

```bash
dotnet build framework/src/BBT.Aether.Npgsql 2>&1 | tail -5
```

Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add framework/src/BBT.Aether.Npgsql/SearchPathCommandInterceptor.cs
git commit -m "feat(npgsql): mode-aware SearchPathCommandInterceptor (TransactionLocal / SessionSearchPath / QualifiedNames stub)"
```

---

### Task 4: `NpgsqlAetherProvider` — accept mode, register cleanup

**Files:**
- Modify: `framework/src/BBT.Aether.Npgsql/NpgsqlAetherProvider.cs`

**Context:** The provider now accepts `SchemaSwitchingMode` (default: `TransactionLocal` for backward compatibility). In `ApplyShared` it creates the interceptor with the mode. For `SessionSearchPath`, it also registers `SchemaScopeState.Cleanup` (idempotent `??=` so only the first DbContext creation sets it). The cleanup runs `RESET search_path` and nulls `state.Current`.

- [ ] **Step 1: Replace the file**

```csharp
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BBT.Aether.Uow.EntityFrameworkCore;

public sealed class NpgsqlAetherProvider(
    SchemaSwitchingMode mode = SchemaSwitchingMode.TransactionLocal) : IAetherDatabaseProvider
{
    public DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

    public void ApplyShared(DbContextOptionsBuilder builder, DbConnection sharedConnection,
        string schema, SchemaScopeState state)
    {
        builder.UseNpgsql(sharedConnection);
        builder.AddInterceptors(new SearchPathCommandInterceptor(schema, state, mode));

        if (mode == SchemaSwitchingMode.SessionSearchPath)
        {
            // Register once per UoW (??= is idempotent across multiple DbContext creations).
            // CompositeUnitOfWork calls this before disposing the connection.
            state.Cleanup ??= static async (conn, ct) =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "RESET search_path";
                await cmd.ExecuteNonQueryAsync(ct);
            };
        }
    }

    public void ApplyConnectionString(DbContextOptionsBuilder builder, string connectionString)
        => builder.UseNpgsql(connectionString);
}
```

Note: the `static` lambda captures nothing — `state.Current = null` is handled by `CompositeUnitOfWork` after invoking the cleanup (see Task 6).

- [ ] **Step 2: Build**

```bash
dotnet build framework/src/BBT.Aether.Npgsql 2>&1 | tail -5
```

Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add framework/src/BBT.Aether.Npgsql/NpgsqlAetherProvider.cs
git commit -m "feat(npgsql): NpgsqlAetherProvider accepts SchemaSwitchingMode, registers SessionSearchPath cleanup"
```

---

### Task 5: Registration extensions — expose mode

**Files:**
- Modify: `framework/src/BBT.Aether.Npgsql/Microsoft/Extensions/DependencyInjection/AetherNpgsqlServiceCollectionExtensions.cs`

- [ ] **Step 1: Add the `mode` parameter**

```csharp
using System;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherNpgsqlServiceCollectionExtensions
{
    /// <summary>
    /// Registers an Aether DbContext backed by PostgreSQL (Npgsql).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="mode">
    /// Schema switching strategy. Default is <see cref="SchemaSwitchingMode.TransactionLocal"/>
    /// (requires <c>IsTransactional = true</c>). Use <see cref="SchemaSwitchingMode.SessionSearchPath"/>
    /// for non-transactional UoWs with Npgsql's native connection pool.
    /// </param>
    /// <param name="configure">Optional additional DbContext options.</param>
    /// <example>
    /// <code>
    /// // Transactional (default):
    /// services.AddAetherNpgsql&lt;MyDbContext&gt;(connectionString);
    ///
    /// // Non-transactional with direct/session pool:
    /// services.AddAetherNpgsql&lt;MyDbContext&gt;(connectionString, SchemaSwitchingMode.SessionSearchPath);
    /// </code>
    /// </example>
    public static IServiceCollection AddAetherNpgsql<TDbContext>(
        this IServiceCollection services,
        string connectionString,
        SchemaSwitchingMode mode = SchemaSwitchingMode.TransactionLocal,
        Action<IServiceProvider, DbContextOptionsBuilder>? configure = null)
        where TDbContext : AetherDbContext<TDbContext>
        => services.AddAetherDbContext<TDbContext>(new NpgsqlAetherProvider(mode), connectionString, configure);
}
```

- [ ] **Step 2: Build and verify existing tests still compile**

```bash
dotnet build framework/BBT.Aether.slnx 2>&1 | tail -10
```

Expected: `Build succeeded.` (The `configure` parameter moved to third position — check if any callers pass it positionally.)

- [ ] **Step 3: Fix any callers that passed `configure` as second positional arg**

```bash
grep -rn "AddAetherNpgsql" framework/ --include="*.cs"
```

If any call site passes a lambda as the second argument (old signature), add `configure:` named parameter or reorder.

- [ ] **Step 4: Commit**

```bash
git add framework/src/BBT.Aether.Npgsql/Microsoft/Extensions/DependencyInjection/AetherNpgsqlServiceCollectionExtensions.cs
git commit -m "feat(npgsql): expose SchemaSwitchingMode in AddAetherNpgsql registration"
```

---

### Task 6: `CompositeUnitOfWork` — IsTransactional flag + cleanup

**Files:**
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/CompositeUnitOfWork.cs`

**Changes:**
1. In `GetDbContextAsync`: wrap `BeginTransactionAsync` in `if (_options.IsTransactional)`, guard `UseTransactionAsync` with `if (_transaction is not null)`.
2. In `DisposeAsync`: invoke `_schemaState.Cleanup` (if set) just before disposing the connection, then null `_schemaState.Current`.

- [ ] **Step 1: Update `GetDbContextAsync` (around line 142)**

Find:
```csharp
        if (_connection is null)
        {
            _connection = configurator.CreateConnection();
            await _connection.OpenAsync(cancellationToken);
            _transaction = await _connection.BeginTransactionAsync(
                _options.IsolationLevel ?? IsolationLevel.ReadCommitted, cancellationToken);

            // A fresh transaction has no per-schema state applied yet.
            _schemaState.Current = null;
        }

        var options = configurator.BuildOptions(_connection, schema, _schemaState);
        var context = ActivatorUtilities.CreateInstance<TDbContext>(serviceProvider, options);

        await context.Database.UseTransactionAsync(_transaction!, cancellationToken);
```

Replace with:
```csharp
        if (_connection is null)
        {
            _connection = configurator.CreateConnection();
            await _connection.OpenAsync(cancellationToken);

            // Reset schema state whenever a fresh connection is established.
            _schemaState.Current = null;

            if (_options.IsTransactional)
            {
                _transaction = await _connection.BeginTransactionAsync(
                    _options.IsolationLevel ?? IsolationLevel.ReadCommitted, cancellationToken);
            }
        }

        var options = configurator.BuildOptions(_connection, schema, _schemaState);
        var context = ActivatorUtilities.CreateInstance<TDbContext>(serviceProvider, options);

        if (_transaction is not null)
        {
            await context.Database.UseTransactionAsync(_transaction, cancellationToken);
        }
```

- [ ] **Step 2: Update `DisposeAsync` — invoke cleanup before connection disposal**

Find the comment `// Dispose every resource...` block. Just before the line `if (_connection is not null)`, insert:

```csharp
        // For SessionSearchPath mode the provider registers a cleanup that resets the session-level
        // search_path before the connection is returned to the pool.
        if (_schemaState.Cleanup is not null && _schemaState.Current is not null && _connection is not null)
        {
            try
            {
                await _schemaState.Cleanup(_connection, CancellationToken.None);
                _schemaState.Current = null;
            }
            catch
            {
                // Swallow — a cleanup failure must not prevent connection disposal.
            }
        }
```

- [ ] **Step 3: Build**

```bash
dotnet build framework/src/BBT.Aether.Infrastructure 2>&1 | tail -5
```

Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add framework/src/BBT.Aether.Infrastructure/BBT/Aether/Uow/CompositeUnitOfWork.cs
git commit -m "feat(uow): respect IsTransactional; invoke SchemaScopeState.Cleanup on dispose"
```

---

### Task 7: Tests

**Files:**
- Modify: `framework/test/BBT.Aether.Postgres.Tests/UnitOfWorkDisposalTests.cs`

**Context:** Three groups of tests:
1. Existing transactional tests still pass (`TransactionLocal` default mode, unchanged behavior).
2. `SessionSearchPath` mode: connection opens without transaction, two queries share the same connection.
3. Schema leakage: after a `SessionSearchPath` UoW disposes, a new UoW on a different schema sees zero rows from the first schema.

The test fixture already builds a provider with `AddAetherNpgsql<TestDbContext>(fx.ConnectionString)` (default `TransactionLocal`). For `SessionSearchPath` tests, build a separate provider with the new mode.

- [ ] **Step 1: Add a second helper that builds a `SessionSearchPath` provider**

Add to the class (after `BuildProvider()`):

```csharp
private IServiceProvider BuildSessionSearchPathProvider()
{
    var services = new ServiceCollection();
    services.AddAetherCore(_ => { });
    services.AddAetherNpgsql<TestDbContext>(fx.ConnectionString, SchemaSwitchingMode.SessionSearchPath);
    return services.BuildServiceProvider();
}
```

- [ ] **Step 2: Add connection-level tests**

```csharp
[Fact]
public async Task SessionSearchPath_opens_connection_without_transaction()
{
    await ArrangeSchemaAsync();
    var sp = BuildSessionSearchPathProvider();

    await using var scope = sp.CreateAsyncScope();
    var ssp = scope.ServiceProvider;
    var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
    var mgr = ssp.GetRequiredService<IUnitOfWorkManager>();
    var provider = ssp.GetRequiredService<IAetherDbContextProvider<TestDbContext>>();

    using (currentSchema.Change(_schema))
    {
        await using var uow = mgr.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = false });

        var db = await provider.GetDbContextAsync();

        db.Database.GetDbConnection().State.ShouldBe(ConnectionState.Open);
        db.Database.CurrentTransaction.ShouldBeNull();

        await uow.CommitAsync();
    }
}

[Fact]
public async Task SessionSearchPath_two_queries_share_same_connection()
{
    await ArrangeSchemaAsync();
    var sp = BuildSessionSearchPathProvider();

    await using var scope = sp.CreateAsyncScope();
    var ssp = scope.ServiceProvider;
    var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
    var mgr = ssp.GetRequiredService<IUnitOfWorkManager>();
    var provider = ssp.GetRequiredService<IAetherDbContextProvider<TestDbContext>>();

    using (currentSchema.Change(_schema))
    {
        await using var uow = mgr.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = false });

        var db1 = await provider.GetDbContextAsync();
        var db2 = await provider.GetDbContextAsync();

        db2.ShouldBeSameAs(db1);
        db2.Database.GetDbConnection().ShouldBeSameAs(db1.Database.GetDbConnection());

        await uow.CommitAsync();
    }
}

[Fact]
public async Task TransactionLocal_still_opens_transaction()
{
    await ArrangeSchemaAsync();
    var sp = BuildProvider(); // default TransactionLocal

    await using var scope = sp.CreateAsyncScope();
    var ssp = scope.ServiceProvider;
    var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
    var mgr = ssp.GetRequiredService<IUnitOfWorkManager>();
    var provider = ssp.GetRequiredService<IAetherDbContextProvider<TestDbContext>>();

    using (currentSchema.Change(_schema))
    {
        await using var uow = mgr.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

        var db = await provider.GetDbContextAsync();

        db.Database.GetDbConnection().State.ShouldBe(ConnectionState.Open);
        db.Database.CurrentTransaction.ShouldNotBeNull();

        await uow.CommitAsync();
    }
}

[Fact]
public async Task TransactionLocal_throws_when_used_without_transaction()
{
    await ArrangeSchemaAsync();
    var sp = BuildProvider(); // TransactionLocal

    await using var scope = sp.CreateAsyncScope();
    var ssp = scope.ServiceProvider;
    var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
    var mgr = ssp.GetRequiredService<IUnitOfWorkManager>();
    var provider = ssp.GetRequiredService<IAetherDbContextProvider<TestDbContext>>();

    using (currentSchema.Change(_schema))
    {
        await using var uow = mgr.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = false });

        // First EF query triggers the interceptor which must throw
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            var db = await provider.GetDbContextAsync();
            await db.Set<Thing>().CountAsync();
        });
    }
}
```

- [ ] **Step 3: Add schema-leakage test**

```csharp
[Fact]
public async Task SessionSearchPath_search_path_reset_prevents_pool_leakage()
{
    var schemaA = "leak_a_" + Guid.NewGuid().ToString("N");
    var schemaB = "leak_b_" + Guid.NewGuid().ToString("N");

    await using (var setupConn = new NpgsqlConnection(fx.ConnectionString))
    {
        await setupConn.OpenAsync();
        await using var cmd = setupConn.CreateCommand();
        cmd.CommandText =
            $"""
             CREATE SCHEMA "{schemaA}";
             CREATE TABLE "{schemaA}".things ("Id" uuid PRIMARY KEY, "Name" text NOT NULL);
             INSERT INTO "{schemaA}".things VALUES (gen_random_uuid(), 'from-a');
             CREATE SCHEMA "{schemaB}";
             CREATE TABLE "{schemaB}".things ("Id" uuid PRIMARY KEY, "Name" text NOT NULL);
             """;
        await cmd.ExecuteNonQueryAsync();
    }

    var sp = BuildSessionSearchPathProvider();
    await using var scope = sp.CreateAsyncScope();
    var ssp = scope.ServiceProvider;
    var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
    var mgr = ssp.GetRequiredService<IUnitOfWorkManager>();
    var provider = ssp.GetRequiredService<IAetherDbContextProvider<TestDbContext>>();

    // First UoW: schema A
    using (currentSchema.Change(schemaA))
    {
        var uowA = mgr.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = false });
        var dbA = await provider.GetDbContextAsync();
        (await dbA.Set<Thing>().CountAsync()).ShouldBe(1);
        await uowA.CommitAsync();
        await uowA.DisposeAsync(); // RESET search_path runs here
    }

    // Second UoW: schema B (0 rows). If RESET was skipped, the pooled connection still points to A.
    using (currentSchema.Change(schemaB))
    {
        await using var uowB = mgr.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = false });
        var dbB = await provider.GetDbContextAsync();
        (await dbB.Set<Thing>().CountAsync())
            .ShouldBe(0, "search_path from the previous UoW must not leak into this one");
        await uowB.CommitAsync();
    }
}
```

- [ ] **Step 4: Run all new tests**

```bash
dotnet test framework/test/BBT.Aether.Postgres.Tests \
  --filter "FullyQualifiedName~SessionSearchPath|FullyQualifiedName~TransactionLocal_throws_when_used_without_transaction|FullyQualifiedName~TransactionLocal_still_opens_transaction" \
  2>&1 | tail -30
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add framework/test/BBT.Aether.Postgres.Tests/UnitOfWorkDisposalTests.cs
git commit -m "test(uow): SchemaSwitchingMode coverage — SessionSearchPath isolation, TransactionLocal guard"
```

---

### Task 8: Full suite

- [ ] **Step 1: Full Postgres tests**

```bash
dotnet test framework/test/BBT.Aether.Postgres.Tests 2>&1 | tail -20
```

Expected: all pass.

- [ ] **Step 2: SqlServer tests**

```bash
dotnet test framework/test/BBT.Aether.SqlServer.Tests 2>&1 | tail -20
```

Expected: all pass.

- [ ] **Step 3: Full build**

```bash
dotnet build framework/BBT.Aether.slnx 2>&1 | tail -10
```

Expected: `Build succeeded.`
