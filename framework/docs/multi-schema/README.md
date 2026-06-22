# Multi-Schema Support

## Overview

Aether supports running a single application against many PostgreSQL **schemas** (one per
tenant, module, or data partition) without baking the schema into the EF Core model. The
active schema is an **immutable working context** selected via a nested, auto-restoring
scope, and a Unit of Work isolates each schema-bound `DbContext` on a single shared
connection/transaction by issuing `SET LOCAL search_path` before every command.

## Database providers

`BBT.Aether.Infrastructure` is **provider-agnostic** — it has no `Npgsql` dependency. The Unit
of Work owns a single shared `DbConnection` / `DbTransaction` and talks to an
[`IAetherDatabaseProvider`](../../src/BBT.Aether.Infrastructure/BBT/Aether/Uow/EntityFrameworkCore/IAetherDatabaseProvider.cs)
seam (connection creation, binding options to the shared connection, and the per-schema
strategy). Pick a provider package:

- **`BBT.Aether.Npgsql`** — PostgreSQL, **full multi-schema** (`NpgsqlAetherProvider` +
  `SearchPathCommandInterceptor` + `PostgreSqlIdentifier`). Register with
  `services.AddAetherNpgsql<MyDbContext>(connectionString);`.
- **`BBT.Aether.SqlServer`** — SQL Server, **single-schema** (`SqlServerAetherProvider`).
  Register with `services.AddAetherSqlServer<MyDbContext>(connectionString);`.

Both wrap the core registration
`services.AddAetherDbContext<MyDbContext>(provider, connectionString, configure?)`; a custom
provider can be registered through that overload directly.

The multi-schema model below (runtime `SET LOCAL search_path` per command) is **PostgreSQL-only**
and lives in `BBT.Aether.Npgsql`. See [SQL Server limitations](#sql-server-limitations).

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

> There is no `ICurrentSchema.Set()`, no `IsResolved` flag, no session-level
> `SET search_path`, and no `NpgsqlSchemaConnectionInterceptor`. Earlier designs used those;
> they have been removed. Schema is applied per command on the shared transaction (see below).

### Schema-name formatting and validation

`Change(schema)` runs the raw name through `ISchemaNameFormatter` first
(`DefaultSchemaNameFormatter` lowercases, replaces spaces/hyphens with `_`, strips other
characters, ensures a leading letter/underscore, and trims to 63 chars). The *formatted*
name is what `Name` returns and what ends up on the connection.

Before a name is interpolated into `SET LOCAL search_path`, it is validated and quoted by
`PostgreSqlIdentifier.QuoteSchema(...)` (regex `^[a-zA-Z_][a-zA-Z0-9_]*$`). An invalid name
throws `InvalidOperationException: Invalid PostgreSQL schema name: <name>`. Schema names
cannot be passed as SQL parameters, so this validate-then-quote step is the injection guard.

## How schema isolation works

Within one Unit of Work, **all** schema-bound `DbContext` instances share **one**
`NpgsqlConnection` and **one** `NpgsqlTransaction` (see
[`CompositeUnitOfWork`](../../src/BBT.Aether.Infrastructure/BBT/Aether/Uow/CompositeUnitOfWork.cs)).
Contexts are created lazily and cached by `(DbContextType, Schema)`; the connection and
transaction are opened on the first context request.

Isolation comes from a `SearchPathCommandInterceptor` that prepends

```sql
SET LOCAL search_path TO "<schema>", public
```

to **every** command issued by a schema-bound context.

### Why per command and not once?

`SET LOCAL` is **transaction-scoped**, not statement-scoped. If the search_path were set
once at context-creation time, a sibling context bound to a different schema (on the *same*
transaction) would overwrite it, and the most recent `SET LOCAL` would then apply to every
subsequent statement regardless of which context issued it. Issuing the search_path as a
prefix on each command guarantees correct per-statement schema resolution.

A `SearchPathState` object tracks the schema currently applied on the shared connection, so
the interceptor **skips the redundant `SET`** when the previous command already targeted the
same schema (the common case) — avoiding a round-trip. A cross-schema switch re-applies it.

The `SET` is run as its own command (same connection + transaction) rather than concatenated
onto the command text, because concatenation would add an extra result set and break EF's
rows-affected accounting for INSERT/UPDATE batches. If a command somehow runs outside the
transaction, the interceptor throws rather than silently letting `SET LOCAL` be ignored.

> Result buffering: a single Npgsql connection cannot have two active readers. Do not stream
> (e.g. `AsAsyncEnumerable` without materializing) across interleaved schema-bound contexts
> within one Unit of Work — EF Core buffers by default, so the normal case is fine.

## EF mappings are schema-agnostic

Map tables with **no schema argument** — schema is resolved at runtime via search_path:

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
serves every schema, and `search_path` selects the schema at execution time.

## Usage

Resolve the schema-bound context from the active Unit of Work via
`IAetherDbContextProvider<TDbContext>` (repositories do this internally). It reads
`currentSchema.Name` and asks the active UoW to materialize the context bound to that schema:

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

The shared-connection model is **safe under PgBouncer transaction pooling** because the
schema is applied with `SET LOCAL`, which is scoped to the transaction. When the transaction
ends and the connection returns to the pool, the search_path is gone — it never leaks into a
connection later handed to another request. This is asserted directly by
[`PgBouncerSearchPathTests`](../../test/BBT.Aether.Postgres.Tests/PgBouncerSearchPathTests.cs)
("SET LOCAL stayed inside the UoW transaction and never mutated session/pooled state").

Rules for safe operation under transaction pooling:

1. **Always run inside an explicit transaction** (`IsTransactional = true`). `SET LOCAL`
   without a transaction would be silently ignored; the interceptor throws if the command
   is not enlisted in a transaction.
2. **Keep transactions short.** A connection is leased to the request only while the
   transaction is open.
3. **No external service calls inside an open transaction** (HTTP, broker publishes, etc.).
   Do that work before opening or after committing the Unit of Work.

## SQL Server limitations

SQL Server is supported via `BBT.Aether.SqlServer` (`SqlServerAetherProvider`), but only as a
**single-schema** provider. It supplies the shared connection/transaction and binds
`UseSqlServer`, but does **not** switch schema per command — SQL Server has no
transaction-scoped `SET LOCAL search_path` equivalent.

- **Single-schema only.** Bind the schema in the model — `modelBuilder.HasDefaultSchema("x")`
  or schema-qualified `ToTable("orders", "x")`. There is no runtime per-command schema switching.
- **Runtime cross-schema-in-one-transaction is PostgreSQL-only.** The multi-schema flow above
  (entering several `currentSchema.Change(...)` scopes and writing across schemas in one
  transaction) relies on the transaction-scoped `SET LOCAL search_path` that SQL Server lacks.
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
