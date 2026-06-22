# Multi-Schema Implementation Notes

> These notes describe the **current** shared-connection / `SET LOCAL search_path`
> implementation. Earlier revisions described a session-level `SET search_path` applied by an
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
        ├── ONE NpgsqlTransaction
        └── DbContext cache keyed by (DbContextType, Schema)
                                   │  each context enlists on the shared tx (UseTransactionAsync)
                                   ▼
   SearchPathCommandInterceptor  ──►  SET LOCAL search_path TO "<schema>", public
        (runs before EVERY command; skips when SearchPathState already == schema)
                                   ▼
                              PostgreSQL
```

## Key design decisions

1. **Schema is a scope, not a setting.** `ICurrentSchema.Change(schema)` pushes a formatted
   schema onto an `AsyncLocal<Stack<string>>` and returns an `IDisposable` that pops it. There
   is no mutable setter and no "is resolved" flag.
   (`BBT.Aether.Core/BBT/Aether/MultiSchema/CurrentSchema.cs`)

2. **One connection + one transaction per Unit of Work.** All schema-bound contexts in a UoW
   share a single `NpgsqlConnection`/`NpgsqlTransaction`, so cross-schema writes commit
   atomically. Contexts are lazily created and cached by `(Type, Schema)`; the connection is
   opened on the first `GetDbContextAsync`.
   (`BBT.Aether.Infrastructure/BBT/Aether/Uow/CompositeUnitOfWork.cs`)

3. **Per-command `SET LOCAL search_path`.** Schema isolation is enforced by
   `SearchPathCommandInterceptor`, which prefixes `SET LOCAL search_path TO "<schema>", public`
   before each command. `SET LOCAL` is transaction-scoped, so a one-time set would be clobbered
   by sibling contexts on the same transaction. A `SearchPathState` skips the redundant `SET`
   when the connection already has the right schema.
   (`.../Uow/EntityFrameworkCore/SearchPathCommandInterceptor.cs`, `SearchPathState.cs`)

4. **Schema-agnostic mappings.** Entities use `ToTable("name")` with no schema, so EF Core
   compiles one model per context type that serves every schema; `search_path` selects the
   schema at runtime.

5. **PostgreSQL only.** `Npgsql` is a direct dependency of `BBT.Aether.Infrastructure`; the UoW
   owns raw Npgsql types. There is no SQL Server schema path in this model.

6. **PgBouncer-safe.** Because schema is applied with `SET LOCAL`, it never leaks to session or
   pooled state. Verified by `PgBouncerSearchPathTests`.

## Wiring

```csharp
services.AddAetherDbContext<MyDbContext>(
    connectionString,
    (sp, options) => options.UseNpgsql(connectionString));
```

`AddAetherDbContext` registers:

- `IAetherDbContextConfigurator<TDbContext>` (`AetherDbContextConfigurator<>`) — captures the
  connection string and the configure delegate; `BuildOptions(sharedConnection)` re-applies the
  configuration then binds to the shared connection via `UseNpgsql(connection)` (this overrides
  the connection-string-based provider call but keeps interceptors such as `AuditInterceptor`).
- The design-time/migrations `DbContext` registration (`AddDbContext`).
- `AddAetherUnitOfWork<TDbContext>()` — ambient accessor (`IAmbientUnitOfWorkAccessor`,
  AsyncLocal singleton), `IUnitOfWorkManager` (scoped), the domain-event sink, and
  `IAetherDbContextProvider<>` (scoped).

The `CompositeUnitOfWork` builds each schema-bound context's options from the configurator and
adds a fresh `SearchPathCommandInterceptor(schema, sharedState)` per context.

## Validation and formatting

- `DefaultSchemaNameFormatter.Format` normalizes the raw name (lowercase, `_` separators,
  strip invalid chars, leading letter/underscore, max 63). `Change` formats before pushing.
- `PostgreSqlIdentifier.QuoteSchema` validates against `^[a-zA-Z_][a-zA-Z0-9_]*$` and quotes the
  name before it is interpolated into `SET LOCAL`. Invalid names throw
  `Invalid PostgreSQL schema name: <name>`.

## Guardrails / common errors

| Message | Cause |
|---------|-------|
| `Current schema is not set.` | `IAetherDbContextProvider.GetDbContextAsync()` called with no active `Change(...)` scope. |
| `No active UnitOfWork.` | No UoW is ambient when a context is requested. |
| `UnitOfWork DbContext limit exceeded. Limit: N` | More than `MaxDbContextCount` distinct `(Type, Schema)` contexts in one UoW (default 16). |
| `Invalid PostgreSQL schema name: X` | Schema name fails the identifier regex. |
| `Schema scope corrupted: out-of-order disposal detected.` | `Change(...)` scopes disposed out of order. |

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
- `PgBouncerSearchPathTests` — `SET LOCAL` does not leak to a fresh/pooled connection.
- `OutboxWithinSharedTransactionTests` — a domain event is written to the outbox inside the same
  shared transaction as the business data (default `AlwaysUseOutbox`).
- `DbContextConfiguratorTests` — `BuildOptions` binds the shared connection and preserves
  interceptors.
