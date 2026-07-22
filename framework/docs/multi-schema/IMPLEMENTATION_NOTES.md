# Multi-Schema Implementation Notes

> These notes describe the **current** qualified-names schema implementation.
> Earlier revisions described the `TransactionLocal` and `SessionSearchPath` switching modes
> (`SET LOCAL search_path` / session `SET search_path` + `RESET search_path`), a session-level
> interceptor (`NpgsqlSchemaConnectionInterceptor`), plus an `ICurrentSchema.Set()` /
> `IsResolved` accessor. Those are gone: qualified names is the only strategy, and no
> `search_path` manipulation happens anywhere. See the corrected design below.

## Design at a glance

```
                  using (currentSchema.Change("flow_a")) { ... }
                                   │  (AsyncLocal stack, auto-restoring)
                                   ▼
   IAetherDbContextProvider<TDbContext>.GetDbContextAsync()
                                   │  reads currentSchema.Name
                                   ▼
   active UnitOfWork (CompositeUnitOfWork)
        ├── IsTransactional = true:  ONE NpgsqlConnection + ONE NpgsqlTransaction
        │                            (opened lazily; every schema-bound context enlists
        │                             on the shared tx via UseTransactionAsync)
        ├── IsTransactional = false: NO physical connection held by the UoW —
        │                            contexts bind UseNpgsql(connectionString) and EF Core
        │                            owns the connection lifecycle (pooled per operation)
        └── DbContext cache keyed by (DbContextType, Schema)
                                   ▼
   QualifiedNamesCommandInterceptor  ──►  rewrite model placeholder / raw {{schema}} token
                                          to the quoted bound schema (no search_path command);
                                          throws if ICurrentSchema.Name differs from the
                                          context's bound schema
                                   ▼
                              PostgreSQL
```

## Key design decisions

1. **Schema is a scope, not a setting.** `ICurrentSchema.Change(schema)` pushes a formatted
   schema onto an `AsyncLocal<Stack<string>>` and returns an `IDisposable` that pops it. There
   is no mutable setter and no "is resolved" flag.
   (`BBT.Aether.Core/BBT/Aether/MultiSchema/CurrentSchema.cs`)

2. **Transactional UoWs share a connection; non-transactional UoWs hold none.** When
   `IsTransactional = true`, all schema-bound contexts in a UoW share a single
   `NpgsqlConnection` and enlist in one shared `NpgsqlTransaction`, so cross-schema writes
   commit atomically; the connection is opened lazily on the first `GetDbContextAsync`. When
   `IsTransactional = false`, `CompositeUnitOfWork.GetDbContextAsync` opens **no** physical
   connection at all: contexts are built via
   `IAetherDbContextConfigurator.BuildOwnedOptions(schema)` →
   `IAetherDatabaseProvider.ApplyOwned(builder, connectionString, schema, currentSchema)`,
   which binds `UseNpgsql(connectionString)` so EF Core owns the connection lifecycle (rents a
   pooled connection per operation and returns it immediately). This reduces connection-pool
   pressure for read-heavy/non-transactional work, but business writes and outbox writes
   cannot be atomic across a process failure. Contexts are lazily created and cached by
   `(Type, Schema)` in both cases.
   (`BBT.Aether.Infrastructure/BBT/Aether/Uow/CompositeUnitOfWork.cs`)

3. **Qualified names is the only isolation strategy.** The former `TransactionLocal` and
   `SessionSearchPath` switching modes were removed, along with all `search_path` manipulation
   (`SET LOCAL search_path`, session `SET search_path`, `RESET search_path` cleanup).
   `SchemaSwitchingMode` now has the single member `QualifiedNames`, and isolation is enforced
   by `QualifiedNamesCommandInterceptor(schema, currentSchema)`:

   - Uses one tenant-independent model placeholder, then rewrites it to the validated schema
     bound to the context immediately before execution. Schema-dependent
     `FromSqlRaw`/`ExecuteSqlRaw` relations use the exact `{{schema}}` token, rewritten to the
     quoted bound schema.
   - Throws if `ICurrentSchema.Name` does not match the context's bound schema (guard against
     a context leaking across schema scopes).
   - Emits no `SET`, `SET LOCAL`, or `RESET search_path` and requires no transaction.

   (`BBT.Aether.Npgsql/QualifiedNamesCommandInterceptor.cs`,
   `.../Uow/EntityFrameworkCore/SchemaSwitchingMode.cs`)

4. **Schema-agnostic mappings.** Entities use `ToTable("name")` with no schema, so EF Core
   compiles one model per context type that serves every schema. Unqualified relations map to
   the constant Aether placeholder, which the interceptor rewrites per context.

5. **Provider-agnostic Infrastructure.** `BBT.Aether.Infrastructure` has no `Npgsql` dependency;
   provider specifics are abstracted behind `IAetherDatabaseProvider`. PostgreSQL support lives in
   `BBT.Aether.Npgsql`, which owns the raw Npgsql types and implements the full multi-schema model
   described above. SQL Server support lives in
   `BBT.Aether.SqlServer` and is single-schema. The mechanism described in this document applies to
   the Npgsql provider.

6. **Safe under any pooling.** Qualified names has no connection schema state at all — nothing
   is ever written to session or transaction state — so it is safe under PgBouncer transaction
   or session pooling as well as the native Npgsql pool. Non-transactional UoWs additionally
   hold no connection at all, so they cannot pin a pooled connection.

7. **Schema-bound object lifetime.** The UoW cache key is `(DbContextType, Schema)`, so one
   repository/service instance can switch `flow_a -> flow_b -> flow_a`. Repositories resolve
   a context for each operation. A previously resolved DbContext, DbSet, or IQueryable must
   not cross scopes; QualifiedNames rejects a bound/current-schema mismatch before DB access.

## Wiring

```csharp
// PostgreSQL — qualified names (the only strategy; safe under any pooling)
services.AddAetherNpgsql<MyDbContext>(connectionString);

// SQL Server (single-schema)
// services.AddAetherSqlServer<MyDbContext>(connectionString);

// Custom provider / advanced
// services.AddAetherDbContext<MyDbContext>(new NpgsqlAetherProvider(), connectionString, configure?);
```

`AddAetherNpgsql(connectionString, mode = SchemaSwitchingMode.QualifiedNames, configure)` still
accepts the optional `mode` parameter, but only for signature compatibility —
`SchemaSwitchingMode.QualifiedNames` is the sole member.

`AddAetherNpgsql` (built on `AddAetherDbContext`) registers:

- `IAetherDbContextConfigurator<TDbContext>` (`AetherDbContextConfigurator<>`) — captures the
  connection string and the configure delegate. For transactional UoWs,
  `BuildOptions(sharedConnection, schema, state)` re-applies the configuration, binds to the
  shared connection via `UseNpgsql(connection)`, and adds a
  `QualifiedNamesCommandInterceptor(schema, currentSchema)` per context. For non-transactional
  UoWs, `BuildOwnedOptions(schema)` → `IAetherDatabaseProvider.ApplyOwned(builder,
  connectionString, schema, currentSchema)` binds `UseNpgsql(connectionString)` instead, so EF
  Core owns the connection lifecycle.
- The design-time/migrations `DbContext` registration (`AddDbContext`).
- `AddAetherUnitOfWork<TDbContext>()` — ambient accessor (`IAmbientUnitOfWorkAccessor`,
  AsyncLocal singleton), `IUnitOfWorkManager` (scoped), the domain-event sink, and
  `IAetherDbContextProvider<>` (scoped).

The provider adds the stable `AetherSchemaModelOptionsExtension`, so
`AetherDbContext` maps unqualified relations under `AetherSchemaModel.Placeholder`. The model
cache marker never includes a tenant name. At command execution the interceptor checks that
`ICurrentSchema.Name` still matches its immutable context binding, rewrites the model
placeholder, and lexically rewrites raw `{{schema}}` tokens. Tokens inside quoted strings or
identifiers, comments, and dollar-quoted bodies are data and remain unchanged.

## Validation and formatting

- `DefaultSchemaNameFormatter.Format` normalizes the raw name (lowercase, `_` separators,
  strip invalid chars, leading letter/underscore, max 63). `Change` formats before pushing.
- `PostgreSqlIdentifier.QuoteSchema` validates against `^[a-zA-Z_][a-zA-Z0-9_]*$`, enforces
  PostgreSQL's identifier-length limit, and quotes the name before it enters command text.
  Invalid names throw
  `Invalid PostgreSQL identifier: <name>`.

## Guardrails / common errors

| Message | Cause |
|---------|-------|
| `Current schema is not set.` | `IAetherDbContextProvider.GetDbContextAsync()` called with no active `Change(...)` scope. |
| `No active UnitOfWork.` | No UoW is ambient when a context is requested. |
| `UnitOfWork DbContext limit exceeded. Limit: N` | More than `MaxDbContextCount` distinct `(Type, Schema)` contexts in one UoW (default 16). |
| `Invalid PostgreSQL identifier: X` | Schema name fails the identifier regex. |
| `Schema scope corrupted: out-of-order disposal detected.` | `Change(...)` scopes disposed out of order. |
| `DbContext is bound to schema 'A', but current schema is 'B'.` | A DbContext/DbSet/IQueryable resolved in one schema scope was executed in another; resolve it again. |

## Background processors

The outbox/inbox processors are single-schema: they read `AetherOutboxOptions.Schema` /
`AetherInboxOptions.Schema`, wrap their work in `currentSchema.Change(options.Schema)`, and use
short `RequiresNew` transactional UoWs (lease → publish-without-transaction → record outcome).
If `Schema` is null/empty they log a warning and no-op. Run one instance per schema.
(`BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxProcessor.cs`)

## Reference tests

These integration tests in `framework/test/BBT.Aether.Postgres.Tests/` are the source of truth
for behavior:

- `MultiSchemaUnitOfWorkTests` — atomic cross-schema commit/rollback, schema isolation via
  qualified names, and the `MaxDbContextCount` guardrail.
- `PgBouncerSearchPathTests` — qualified names never mutate the session `search_path`, so no
  schema state can leak to a fresh/pooled connection.
- `UnitOfWorkDisposalTests` — non-transactional context leaves connection management to EF
  Core (the UoW holds no physical connection); schema does not leak across units of work.
- `OutboxWithinSharedTransactionTests` — a domain event is written to the outbox inside the same
  shared transaction as the business data (default `AlwaysUseOutbox`).
- `DbContextConfiguratorTests` — `BuildOptions` binds the shared connection and preserves
  interceptors.
- `QualifiedNamesTests` — repository reuse across schema scopes, context mismatch rejection,
  qualified EF SQL, raw token lexical handling, and no search-path commands.
