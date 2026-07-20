# Multi-Schema Implementation Notes

> These notes describe the **current** shared-connection, mode-aware schema implementation.
> Earlier revisions described a session-level `SET search_path` applied by an
> `NpgsqlSchemaConnectionInterceptor`, plus an `ICurrentSchema.Set()` / `IsResolved` accessor.
> Those are gone. See the corrected design below.

## Design at a glance

```
                  using (currentSchema.Change("flow_a")) { ... }
                                   │  (AsyncLocal stack, auto-restoring)
                                   ▼
   IAetherDbContextProvider<TDbContext>.GetDbContextAsync()
                                   │  reads currentSchema.Name
                                   ▼
   active UnitOfWork (CompositeUnitOfWork)
        ├── ONE NpgsqlConnection
        ├── ONE NpgsqlTransaction  (only when IsTransactional = true)
        ├── SchemaScopeState       (shared; tracks Current schema + optional Cleanup delegate)
        └── DbContext cache keyed by (DbContextType, Schema)
                                   │  transactional UoW: each context enlists on the shared tx
                                   │  (UseTransactionAsync)
                                   ▼
   SearchPathCommandInterceptor  ──►  mode-aware command handling
        SchemaSwitchingMode.TransactionLocal:  SET LOCAL search_path TO "<schema>", public
                                               (per command; skips via SchemaScopeState.Current)
        SchemaSwitchingMode.SessionSearchPath: SET search_path TO "<schema>", public
                                               (once per UoW; skips if same schema)
                                               + RESET search_path on UoW dispose (via Cleanup)
        SchemaSwitchingMode.QualifiedNames:    rewrite model placeholder / raw {{schema}} token
                                               (no search_path command)
                                   ▼
                              PostgreSQL
```

## Key design decisions

1. **Schema is a scope, not a setting.** `ICurrentSchema.Change(schema)` pushes a formatted
   schema onto an `AsyncLocal<Stack<string>>` and returns an `IDisposable` that pops it. There
   is no mutable setter and no "is resolved" flag.
   (`BBT.Aether.Core/BBT/Aether/MultiSchema/CurrentSchema.cs`)

2. **One connection, plus one transaction when requested, per Unit of Work.** All schema-bound
   contexts in a UoW share a single `NpgsqlConnection`. When `IsTransactional = true`, they also
   enlist in one shared `NpgsqlTransaction`, so cross-schema writes commit atomically. When
   `IsTransactional = false`, no transaction is created: business writes and outbox writes cannot
   be atomic across a process failure. Contexts are lazily created and cached by `(Type, Schema)`;
   the connection is opened on the first `GetDbContextAsync`.
   (`BBT.Aether.Infrastructure/BBT/Aether/Uow/CompositeUnitOfWork.cs`)

3. **Mode-aware isolation via `SchemaSwitchingMode`.** Schema isolation is enforced by a
   mode-aware `SearchPathCommandInterceptor` configured at registration time via
   `AddAetherNpgsql(connectionString, mode)` (default `TransactionLocal`).

   - `TransactionLocal`: Prefixes `SET LOCAL search_path TO "<schema>", public` before each
     command. `SET LOCAL` is transaction-scoped — requires `IsTransactional = true`. A
     `SchemaScopeState.Current` field skips the redundant `SET` when the connection already
     has the right schema. Throws `InvalidOperationException` if no transaction is open
     (guard against misconfiguration).
   - `SessionSearchPath`: Issues `SET search_path` once per UoW, skipping repeats via
     `SchemaScopeState.Current`. Registers a `SchemaScopeState.Cleanup` delegate
     (`RESET search_path`) that `CompositeUnitOfWork.DisposeAsync` invokes before the
     connection is returned to the pool, preventing session-state leakage. Does not require
     a transaction (`IsTransactional = false`).
   - `QualifiedNames`: Uses one tenant-independent model placeholder, then rewrites it to the
     validated schema bound to the context immediately before execution. Schema-dependent
     `FromSqlRaw`/`ExecuteSqlRaw` relations use the exact `{{schema}}` token. It emits no
     `SET`, `SET LOCAL`, or `RESET search_path` and does not require a transaction.

   (`.../Uow/EntityFrameworkCore/SearchPathCommandInterceptor.cs`, `SchemaScopeState.cs`,
   `SchemaSwitchingMode.cs`)

4. **Schema-agnostic mappings.** Entities use `ToTable("name")` with no schema, so EF Core
   compiles one model per context type that serves every schema. Search-path modes resolve
   unqualified relations through connection state; QualifiedNames gives them the constant
   Aether placeholder and rewrites it per context.

5. **Provider-agnostic Infrastructure.** `BBT.Aether.Infrastructure` has no `Npgsql` dependency;
   provider specifics are abstracted behind `IAetherDatabaseProvider`. PostgreSQL support lives in
   `BBT.Aether.Npgsql`, which owns the raw Npgsql types and implements the full multi-schema model
   described above. SQL Server support lives in
   `BBT.Aether.SqlServer` and is single-schema. The mechanism described in this document applies to
   the Npgsql provider.

6. **PgBouncer-safe choices.** `TransactionLocal` never leaks because PostgreSQL reverts
   `SET LOCAL` with the transaction. `QualifiedNames` has no connection schema state at all.
   `SessionSearchPath` remains limited to a session-pinned/native connection.

7. **Schema-bound object lifetime.** The UoW cache key is `(DbContextType, Schema)`, so one
   repository/service instance can switch `flow_a -> flow_b -> flow_a`. Repositories resolve
   a context for each operation. A previously resolved DbContext, DbSet, or IQueryable must
   not cross scopes; QualifiedNames rejects a bound/current-schema mismatch before DB access.

## Wiring

```csharp
// PostgreSQL — TransactionLocal (default, PgBouncer-safe)
services.AddAetherNpgsql<MyDbContext>(connectionString);
// or explicitly:
services.AddAetherNpgsql<MyDbContext>(connectionString, SchemaSwitchingMode.TransactionLocal);

// PostgreSQL — SessionSearchPath (non-transactional, native pool only)
services.AddAetherNpgsql<MyDbContext>(connectionString, SchemaSwitchingMode.SessionSearchPath);

// PostgreSQL — QualifiedNames (transaction optional, no connection schema state)
services.AddAetherNpgsql<MyDbContext>(connectionString, SchemaSwitchingMode.QualifiedNames);

// SQL Server (single-schema)
// services.AddAetherSqlServer<MyDbContext>(connectionString);

// Custom provider / advanced
// services.AddAetherDbContext<MyDbContext>(new NpgsqlAetherProvider(mode), connectionString, configure?);
```

`AddAetherNpgsql` (built on `AddAetherDbContext`) registers:

- `IAetherDbContextConfigurator<TDbContext>` (`AetherDbContextConfigurator<>`) — captures the
  connection string and the configure delegate; `BuildOptions(sharedConnection, schema, state)`
  re-applies the configuration, binds to the shared connection via `UseNpgsql(connection)`, and
  adds a `SearchPathCommandInterceptor(schema, state, mode, currentSchema)` per context.
- The design-time/migrations `DbContext` registration (`AddDbContext`).
- `AddAetherUnitOfWork<TDbContext>()` — ambient accessor (`IAmbientUnitOfWorkAccessor`,
  AsyncLocal singleton), `IUnitOfWorkManager` (scoped), the domain-event sink, and
  `IAetherDbContextProvider<>` (scoped).

`NpgsqlAetherProvider.ApplyShared` also registers `SchemaScopeState.Cleanup` when mode is
`SessionSearchPath`: the cleanup delegate issues `RESET search_path` and is invoked once by
`CompositeUnitOfWork.DisposeAsync` before releasing the connection to the pool.

In QualifiedNames mode the provider adds the stable `AetherSchemaModelOptionsExtension`, so
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
| `DbContext is bound to schema 'A', but current schema is 'B'.` | A DbContext/DbSet/IQueryable resolved in one QualifiedNames scope was executed in another; resolve it again. |
| `Raw SQL token '{{schema}}' requires SchemaSwitchingMode.QualifiedNames.` | The explicit token was used with a search-path mode. |

## Background processors

The outbox/inbox processors are single-schema: they read `AetherOutboxOptions.Schema` /
`AetherInboxOptions.Schema`, wrap their work in `currentSchema.Change(options.Schema)`, and use
short `RequiresNew` transactional UoWs (lease → publish-without-transaction → record outcome).
If `Schema` is null/empty they log a warning and no-op. Run one instance per schema.
(`BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxProcessor.cs`)

## Reference tests

These integration tests in `framework/test/BBT.Aether.Postgres.Tests/` are the source of truth
for behavior:

- `MultiSchemaUnitOfWorkTests` — atomic cross-schema commit/rollback, isolation via search_path,
  the SET-skip optimization, and the `MaxDbContextCount` guardrail.
- `PgBouncerSearchPathTests` — `SET LOCAL` (`TransactionLocal` mode) does not leak to a
  fresh/pooled connection.
- `UnitOfWorkDisposalTests` — `SessionSearchPath` mode: connection without transaction, shared
  context caching, `RESET search_path` at dispose (pool leakage prevention); `TransactionLocal`
  mode: transaction is opened, throws correctly when used without `IsTransactional = true`.
- `OutboxWithinSharedTransactionTests` — a domain event is written to the outbox inside the same
  shared transaction as the business data (default `AlwaysUseOutbox`).
- `DbContextConfiguratorTests` — `BuildOptions` binds the shared connection and preserves
  interceptors.
- `QualifiedNamesTests` — repository reuse across schema scopes, context mismatch rejection,
  qualified EF SQL, raw token lexical handling, and no search-path commands.
