# Multi-Schema Support

## Overview

Aether supports running a single application against many PostgreSQL **schemas** (one per
tenant, module, or data partition) without baking the schema into the EF Core model. The
active schema is an **immutable working context** selected via a nested, auto-restoring
scope, and a Unit of Work isolates each schema-bound `DbContext` on a single shared
connection by either applying a `search_path` strategy or qualifying every mapped relation.

The strategy is explicit and pool-topology-aware: pick a `SchemaSwitchingMode` that matches
how your connection pool is configured (see [Schema switching modes](#schema-switching-modes)).

## Provider support

Multi-schema (per-request schema switching via `ICurrentSchema.Change`) is supported **only on the
PostgreSQL provider** (`BBT.Aether.Npgsql`), which supports transaction-local and session-level
`search_path` strategies as well as runtime-qualified relation names.

**SQL Server (`BBT.Aether.SqlServer`) is single-schema.** It always uses the schema fixed in the EF
model (`HasDefaultSchema` / schema-qualified `ToTable`). On SQL Server, `ICurrentSchema.Change(...)`
does not change the schema used for queries — the call is effectively a no-op for schema resolution.
Applications requiring true multi-schema isolation must use PostgreSQL.

## Database providers

`BBT.Aether.Infrastructure` is **provider-agnostic** — it has no `Npgsql` dependency. The Unit
of Work owns a single shared `DbConnection` and, when `IsTransactional = true`, a single shared
`DbTransaction`. It talks to an
[`IAetherDatabaseProvider`](../../src/BBT.Aether.Infrastructure/BBT/Aether/Uow/EntityFrameworkCore/IAetherDatabaseProvider.cs)
seam (connection creation, binding options to the shared connection, and the per-schema
strategy). Pick a provider package:

- **`BBT.Aether.Npgsql`** — PostgreSQL, **full multi-schema** (`NpgsqlAetherProvider` +
  `SearchPathCommandInterceptor` + `PostgreSqlIdentifier`). Register with
  `services.AddAetherNpgsql<MyDbContext>(connectionString);` (optionally pass a `SchemaSwitchingMode`).
- **`BBT.Aether.SqlServer`** — SQL Server, **single-schema** (`SqlServerAetherProvider`).
  Register with `services.AddAetherSqlServer<MyDbContext>(connectionString);`.

Both wrap the core registration
`services.AddAetherDbContext<MyDbContext>(provider, connectionString, configure?)`; a custom
provider can be registered through that overload directly.

The multi-schema model below is **PostgreSQL-only** and lives in `BBT.Aether.Npgsql`. See
[SQL Server limitations](#sql-server-limitations).

## The current schema is a scope, not a setting

`ICurrentSchema` exposes the active schema as a stack of scopes backed by `AsyncLocal`. You
do not *set* a schema; you *enter* one with `Change(...)`, which returns an `IDisposable`
that restores the previous schema on dispose:

```csharp
public interface ICurrentSchema
{
    // Top-of-stack schema name, or null if no scope is active.
    string? Name { get; }

    // Push a schema; dispose the returned token to pop it.
    IDisposable Change(string schema);
}
```

```csharp
using (currentSchema.Change("flow_a"))
{
    // currentSchema.Name == "flow_a"  (after formatting)
    using (currentSchema.Change("flow_b"))
    {
        // currentSchema.Name == "flow_b"
    }
    // back to "flow_a"
}
// back to null
```

The scope flows across `await` boundaries (`AsyncLocal`) and restores the previous value on
dispose. Out-of-order disposal throws `InvalidOperationException` ("Schema scope corrupted").

> The obsolete `ICurrentSchema.Set()` / `IsResolved` API and
> `NpgsqlSchemaConnectionInterceptor` have been removed, as have the former `TransactionLocal`
> and `SessionSearchPath` switching modes. Schema targeting is always
> [qualified names](#schema-switching-qualified-names): relations are qualified in the SQL
> itself and `search_path` is never touched.

### Schema-name formatting and validation

`Change(schema)` runs the raw name through `ISchemaNameFormatter` first
(`DefaultSchemaNameFormatter` lowercases, replaces spaces/hyphens with `_`, strips other
characters, ensures a leading letter/underscore, and trims to 63 chars). The *formatted*
name is what `Name` returns and what ends up on the connection.

Before a name is interpolated into SQL, it is validated and quoted by
`PostgreSqlIdentifier.QuoteSchema(...)` (regex `^[a-zA-Z_][a-zA-Z0-9_]*$`). An invalid name
throws `InvalidOperationException: Invalid PostgreSQL identifier: <name>`. Schema names
cannot be passed as SQL parameters, so this validate-then-quote step is the injection guard.

## Schema switching: qualified names

Schema targeting always uses `SchemaSwitchingMode.QualifiedNames` — the only member of the
enum (the former `TransactionLocal` and `SessionSearchPath` modes were removed together with
their `search_path` manipulation). The parameter on `AddAetherNpgsql` is optional and kept for
signature compatibility:

```csharp
services.AddAetherNpgsql<MyDbContext>(connectionString);
```

| Strategy | Command issued | Requires transaction | Pool topology |
|----------|---------------|----------------------|---------------|
| `QualifiedNames` | Rewrites EF model placeholders and explicit raw-SQL `{{schema}}` tokens to `"<schema>"` | No | PgBouncer transaction/session pooling ✅, native pool ✅ |

Because every command is self-describing, no connection-level state exists: queries are
schema-safe on any pooled connection, transactional or not, with or without PgBouncer.

## How schema isolation works

Connection topology depends on the Unit of Work's transaction mode (see
[`CompositeUnitOfWork`](../../src/BBT.Aether.Infrastructure/BBT/Aether/Uow/CompositeUnitOfWork.cs)):

- **`IsTransactional = true`:** all schema-bound `DbContext` instances share **one**
  `NpgsqlConnection` and **one** `NpgsqlTransaction`, opened on the first context request.
  Cross-schema writes commit atomically.
- **`IsTransactional = false`:** the Unit of Work holds **no physical connection**. Contexts
  are bound to the connection string and EF Core owns the connection lifecycle — a pooled
  connection is rented per operation and returned immediately. This keeps connection-pool
  pressure proportional to actual database work, not to request duration.

Contexts are created lazily and cached by `(DbContextType, Schema)` in both shapes.

Isolation comes from `QualifiedNamesCommandInterceptor`, which runs before every EF command:

Schema-agnostic mappings receive one tenant-independent model placeholder. Immediately before
execution the interceptor replaces that placeholder with the validated schema bound to the
`DbContext`; the EF model cache therefore does not grow per tenant. A context resolved under
`flow_a` remains bound to `flow_a` even if the ambient scope later changes.

Repository and service instances may be reused across schema scopes because each repository
operation resolves the appropriate context. A resolved `DbContext`, `DbSet`, or `IQueryable`
must not cross schema scopes. Executing one under a different current schema fails before
database access; resolve it again inside the new `Change(...)` scope.

> Result buffering: a single Npgsql connection cannot have two active readers. Do not stream
> (e.g. `AsAsyncEnumerable` without materializing) across interleaved schema-bound contexts
> within one Unit of Work — EF Core buffers by default, so the normal case is fine.

## EF mappings are schema-agnostic

Map tables with **no schema argument** — schema is resolved at runtime by qualified-names
rewriting:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);

    modelBuilder.Entity<Order>(e =>
    {
        e.ToTable("orders");            // NOT ToTable("orders", "flow_a")
        e.HasKey(o => o.Id);
        e.Property(o => o.Customer).IsRequired();
    });
}
```

**Why:** EF Core caches one compiled model per `DbContext` type. If the schema were part of
the mapping, you would need a distinct model (and cache entry) per schema, and a context
could only ever talk to one schema. Leaving tables unqualified means the same compiled model
serves every schema, and the interceptor supplies the schema at execution time.

## Usage

Resolve the schema-bound context from the active Unit of Work via
`IAetherDbContextProvider<TDbContext>` (repositories do this internally). It reads
`currentSchema.Name` and asks the active UoW to materialize the context bound to that schema.

### Transactional (shared connection + transaction)

```csharp
using (currentSchema.Change("flow_a"))
await using (var uow = unitOfWorkManager.Begin(
    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
{
    var db = await dbContextProvider.GetDbContextAsync();   // bound to flow_a on the shared tx
    db.Set<Thing>().Add(new Thing { Id = Guid.NewGuid(), Name = "a" });

    // Cross-schema work in the SAME transaction: enter another scope, resolve again.
    using (currentSchema.Change("flow_b"))
    {
        var dbB = await dbContextProvider.GetDbContextAsync();  // bound to flow_b, same tx
        dbB.Set<Thing>().Add(new Thing { Id = Guid.NewGuid(), Name = "b" });
    }

    await uow.CommitAsync();   // flow_a + flow_b commit atomically (single transaction)
}
```

### Non-transactional (EF Core-owned connections)

Use `IsTransactional = false` for read-heavy flows. The Unit of Work never opens a physical
connection; EF Core rents one from the pool per operation and returns it immediately:

```csharp
using (currentSchema.Change("flow_a"))
await using (var uow = unitOfWorkManager.Begin(
    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = false }))
{
    var db = await dbContextProvider.GetDbContextAsync();  // no connection opened yet
    var items = await db.Set<Thing>().ToListAsync();       // rents + returns a pooled connection

    // Switch schema within same UoW:
    using (currentSchema.Change("flow_b"))
    {
        var dbB = await dbContextProvider.GetDbContextAsync();
        var others = await dbB.Set<Thing>().ToListAsync(); // fully-qualified against flow_b
    }
}
```

### Raw SQL and the `{{schema}}` token

```csharp
await using var uow = uowManager.Begin(new UnitOfWorkOptions
{
    Scope = UnitOfWorkScopeOption.RequiresNew,
    IsTransactional = true
});

using (currentSchema.Change("tenant_a"))
{
    await repository.GetListAsync();
}

using (currentSchema.Change("tenant_b"))
{
    // The same injected repository/service instance can be reused here.
    var rows = await repository.GetListAsync();
    var db = await dbContextProvider.GetDbContextAsync();

    await db.Database.ExecuteSqlRawAsync(
        "UPDATE {{schema}}.\"orders\" SET \"Status\" = {0}",
        status);
}

await uow.CommitAsync();
```

Schema-dependent `FromSqlRaw` and `ExecuteSqlRaw` statements must put the exact `{{schema}}`
token at every runtime relation reference. The token is replaced only in PostgreSQL SQL code;
occurrences inside string literals (including escape strings), quoted identifiers, line or
nested block comments, and dollar-quoted bodies are preserved. Parameters remain parameters.
Schema-independent SQL such as `SELECT 1` needs no token.

If `currentSchema.Name` is null when a context is requested, the provider throws
`InvalidOperationException: Current schema is not set.`

### HTTP request path

For ASP.NET Core, register schema resolution and the middleware. `SchemaResolutionMiddleware`
resolves the schema from the request and wraps the rest of the pipeline in
`currentSchema.Change(schema)`; the `[UnitOfWork]` aspect / UoW middleware opens the Unit of
Work. Your controllers/services simply resolve repositories or `IAetherDbContextProvider<T>`.

```csharp
builder.Services.AddSchemaResolution(options =>
{
    options.HeaderKey = "X-Schema";      // from header
    options.QueryStringKey = "schema";   // from query string
    options.RouteValueKey = "schema";    // from route
    options.ThrowIfNotFound = true;      // 400 if missing
});

var app = builder.Build();
app.UseRouting();
app.UseSchemaResolution();          // after UseRouting; wraps the request in Change(schema)
app.UseUnitOfWorkMiddleware();      // after schema is established
app.MapControllers();
```

## PgBouncer (transaction pooling)

Qualified names are safe under PgBouncer transaction pooling: no command depends on backend
session state, so it does not matter which physical backend executes it, transactional or not.

General rules that keep any pool topology healthy:

1. **Keep transactions short.** A transactional Unit of Work leases one connection for its
   whole lifetime; a non-transactional one leases none.
2. **No external service calls inside an open transaction** (HTTP, broker publishes, etc.).
   Do that work before opening or after committing the Unit of Work.

## SQL Server limitations

SQL Server is supported via `BBT.Aether.SqlServer` (`SqlServerAetherProvider`), but only as a
**single-schema** provider. It supplies the shared connection/transaction and binds
`UseSqlServer`, but does **not** implement the PostgreSQL provider's runtime relation
qualification or schema-switching mechanisms.

- **Single-schema only.** Bind the schema in the model — `modelBuilder.HasDefaultSchema("x")`
  or schema-qualified `ToTable("orders", "x")`. There is no runtime per-command schema switching.
- **Runtime cross-schema-in-one-transaction is PostgreSQL-only.** The multi-schema flow above
  (entering several `currentSchema.Change(...)` scopes and writing across schemas in one
  transaction) is provided by PostgreSQL's `TransactionLocal`, `SessionSearchPath`, and
  `QualifiedNames` modes. The SQL Server provider has no equivalent runtime relation
  rewriting/schema-switching support.
- **Outbox/Inbox is not yet supported on SQL Server.** Processing currently uses
  PostgreSQL-specific lease SQL (`FOR UPDATE SKIP LOCKED`, in `EfCoreOutboxStore` /
  `EfCoreInboxStore`). SQL Server support is a follow-up.

## Background pollers are single-schema

The outbox/inbox processors operate on **one configured schema per instance**. Set the
schema on the options:

```csharp
services.Configure<AetherOutboxOptions>(o => o.Schema = "flow_a");
services.Configure<AetherInboxOptions>(o => o.Schema = "flow_a");
```

`AetherOutboxOptions.Schema` / `AetherInboxOptions.Schema` (in `BBT.Aether.Events`) tells the
processor which schema's table to handle; it opens a UoW bound to that schema via
`currentSchema.Change(options.Schema)` on every run. There is no ambient schema in a
background worker, so if `Schema` is null/empty the processor logs a warning and does
nothing. For multi-schema deployments, **run one processor instance per schema**.

## Related Features

- [Unit of Work](../unit-of-work/README.md) — shared-connection transaction management
- [Repository Pattern](../repository-pattern/README.md) — data access
- [Domain Events](../domain-events/README.md) — outbox dispatch within the shared transaction
