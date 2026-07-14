# Qualified Names and Unit of Work Event Semantics Design

## Goal

Implement `SchemaSwitchingMode.QualifiedNames` for runtime multi-schema PostgreSQL access, including an explicit raw-SQL schema token, and make every non-transactional Unit of Work dispatch buffered domain events only from `CommitAsync` without an opt-in flag.

The design must preserve Aether's runtime `ICurrentSchema.Change(...)` behavior. A single service and repository instance may access `tenant_a`, temporarily switch to `tenant_b`, and then return to `tenant_a` inside one request and one Unit of Work.

## Constraints

- PostgreSQL schema selection is resolved at runtime from `ICurrentSchema.Name`.
- A Unit of Work may contain multiple schema-bound DbContext instances.
- A DbContext, DbSet, or IQueryable is bound to the schema under which it was resolved and must not be reused under another schema scope.
- Qualified names must not manipulate `search_path` or issue `SET`, `SET LOCAL`, or `RESET` commands.
- Arbitrary PostgreSQL SQL is not rewritten with regular expressions. Raw SQL uses an explicit `{{schema}}` token.
- Schema identifiers are validated and quoted before they enter SQL.
- Non-transactional business writes and outbox writes are not atomic. The API and documentation must state this explicitly.
- Existing `TransactionLocal` and `SessionSearchPath` behavior remains supported.

## QualifiedNames Architecture

### Runtime context selection

`AetherDbContextProvider<TDbContext>.GetDbContextAsync()` reads `ICurrentSchema.Name` on every call. `CompositeUnitOfWork` continues to cache contexts by `DbContextKey(DbContextType, Schema)`.

This makes a constructor-injected repository reusable across schema scopes because `EfCoreRepository` stores the provider, not a DbContext. Each repository operation resolves the context again:

```csharp
using (currentSchema.Change("tenant_a"))
{
    await repository.GetListAsync();
}

using (currentSchema.Change("tenant_b"))
{
    await repository.GetListAsync();
}
```

The first call uses the `tenant_a` context. The second uses a distinct `tenant_b` context. Returning to `tenant_a` reuses the first context from the Unit of Work cache.

### EF-generated SQL

QualifiedNames mode uses one constant internal model placeholder for mappings that do not specify a schema:

```sql
"__aether_schema__"."orders"
```

The placeholder is model-level state, not a tenant name. This avoids a separate EF model cache entry for every tenant. A schema-bound command interceptor captures the immutable schema passed to `BuildOptions(...)` when the context is created and replaces the quoted internal placeholder with the validated, quoted runtime schema immediately before command execution.

For a context bound to `tenant_b`, EF-generated SQL therefore becomes:

```sql
SELECT ... FROM "tenant_b"."orders"
```

Mappings that explicitly name a non-placeholder schema remain unchanged. Schema-agnostic Aether mappings receive the runtime placeholder. QualifiedNames options must be distinguishable from search-path options in EF model caching, but the cache key must not contain the tenant schema.

### Raw SQL

`FromSqlRaw` and `ExecuteSqlRaw` SQL must opt into runtime schema substitution with the exact `{{schema}}` token:

```csharp
context.Orders.FromSqlRaw(
    "SELECT * FROM {{schema}}.\"orders\" WHERE \"Status\" = {0}",
    status);

context.Database.ExecuteSqlRaw(
    "UPDATE {{schema}}.\"orders\" SET \"Status\" = {0}",
    status);
```

In QualifiedNames mode the interceptor replaces every `{{schema}}` occurrence in PostgreSQL SQL code with the same validated and quoted schema used for EF-generated SQL. Parameters and all other SQL text remain unchanged. The lightweight lexical scanner preserves token-shaped text inside normal and escape string literals, quoted identifiers, line comments, nested block comments, and dollar-quoted bodies. Repeated code-region tokens support joins, CTEs, and subqueries without parsing relation syntax.

Schema-independent commands such as `SELECT 1` remain valid without a token. Aether does not attempt to infer whether arbitrary SQL is schema-dependent. Documentation requires `{{schema}}` for every schema-dependent raw SQL relation reference.

An unresolved Aether schema placeholder is rejected before the command reaches PostgreSQL. Error messages identify the mode, bound schema, and remediation without including full SQL or parameter values.

### Context/scope mismatch guard

Each QualifiedNames interceptor is bound to the schema used to create its DbContext. Before execution it compares that schema with the current `ICurrentSchema.Name`.

If a DbContext, DbSet, or IQueryable obtained under `tenant_a` is executed while `tenant_b` is current, execution fails before database access with an error explaining that the context must be resolved again inside the new schema scope. This prevents change-tracker identity collisions and cross-tenant writes.

Repository methods remain convenient because they resolve a context for each operation. Custom repositories must not cache a DbContext, DbSet, or IQueryable across schema scopes.

### Framework-owned raw SQL

The Npgsql outbox, inbox, and background-job lease stores create ADO.NET commands directly from the shared connection. Those commands do not pass through EF's command interceptor, so they must handle the selected mode explicitly.

All three stores build the full relation name from the context-bound/current schema and EF table metadata, using shared `PostgreSqlIdentifier` helpers for both schema and table identifiers. This is safe in every switching mode and avoids relying on EF interceptors for commands that bypass EF.

The stores no longer issue their own `SET LOCAL` commands. In particular, this avoids a `SessionSearchPath` raw-command path setting session state without updating `SchemaScopeState`, which could otherwise bypass Unit of Work cleanup. Direct identifier interpolation is removed.

## Unit of Work and Domain Events

### Schema-bound event buffering

Events collected from a DbContext are buffered with the schema to which that context is bound:

```text
PendingDomainEvent
  Schema: tenant_b
  Envelope: OrderCreated
```

The context-specific `ILocalTransactionEventEnqueuer` captures the schema when `CompositeUnitOfWork` creates the context. Event dispatch processes contiguous schema runs and enters `ICurrentSchema.Change(schema)` for each run. This preserves exact production order (for example, A1, B1, A2) while avoiding per-event scope churn where adjacent events share a schema.

This ensures that:

- the outbox row is written to the event-producing schema;
- the CloudEvent `Schema` property contains the event-producing schema;
- commit-time ambient schema changes cannot redirect buffered events;
- events from several schemas can be committed by one root Unit of Work.

### SaveChangesAsync contract

`SaveChangesAsync` persists pending entity changes and transfers domain events from tracked aggregates into the Unit of Work's schema-bound buffer. It never publishes to a broker and never writes buffered domain events to the outbox merely because SaveChanges was called.

### Transactional commit

For `AlwaysUseOutbox`:

1. Save all pending business changes.
2. Dispatch buffered events by schema into the corresponding outbox DbContext.
3. Save the generated outbox rows within the shared transaction.
4. Commit the shared transaction.
5. Clear the event buffer and invoke completed handlers.

Any dispatch or outbox write failure propagates and prevents transaction commit. `DomainEventDispatcher` must not swallow per-event outbox failures.

For `PublishWithFallback`:

1. Save pending changes and commit the shared transaction.
2. Publish events directly, grouped under their producing schema.
3. If direct publish fails, write the affected events to that schema's outbox in a new `RequiresNew` Unit of Work.
4. Clear only successfully published or durably stored events.

### Non-transactional commit

The historical non-transactional dispatch opt-in is removed. Buffered events in every non-transactional Unit of Work are handled by `CommitAsync` without configuration.

For `AlwaysUseOutbox`:

1. Save all pending business changes. Each database save may auto-commit.
2. Dispatch buffered events by schema into the corresponding outbox contexts.
3. Save outbox rows.
4. Clear the event buffer and invoke completed handlers.

For `PublishWithFallback`, pending database changes are saved first and direct publication begins only inside `CommitAsync`; failures use the same schema-aware outbox fallback as the transactional path.

Because no shared database transaction exists, a crash can occur between a business write and its outbox write. The framework guarantees commit-boundary dispatch and failure propagation, not atomicity between those writes. Consumers must retain idempotent processing and recovery practices.

If buffered events exist but `IDomainEventDispatcher` is not registered, `CommitAsync` fails instead of silently dropping events. A failed commit does not mark the Unit of Work completed. Buffered events are cleared only after the selected delivery path succeeds.

## Nested Unit of Work Semantics

A participating `Required` scope does not own the shared root and therefore cannot physically commit it.

- `CommitAsync` on a participating inner `Required` scope records no physical commit; the owning outer scope performs the root commit.
- `RollbackAsync` on a participating inner scope aborts the root. A later outer commit fails, and owner disposal rolls back/releases resources.
- `SaveChangesAsync` may still flush changes into the root's current transaction or, for a non-transactional root, persist them according to the documented non-transactional semantics.
- A transactional inner `Required` scope cannot join a non-transactional outer root. It fails with guidance to use `RequiresNew`.
- `RequiresNew` creates and owns an isolated root and retains independent commit/rollback behavior.

The root's effective transaction mode is fixed at initialization. `UnitOfWorkOptions.IsTransactional` documentation must not claim that a transaction can be escalated later; `EnsureTransactionAsync` is not an escalation mechanism in the current shared-connection design.

## Error Handling

- Invalid or over-length schema identifiers fail before command execution through `PostgreSqlIdentifier`.
- QualifiedNames context/current-schema mismatch fails before database access.
- Outbox dispatch errors propagate to `CommitAsync`.
- Missing domain-event infrastructure with buffered events fails instead of dropping data silently.
- Error messages do not include complete SQL text, event payloads, or parameter values.
- Completed and failed handlers retain their once-only behavior.

## Verification Strategy

Implementation follows test-driven development. Each behavior is introduced by a targeted failing test before production changes.

### QualifiedNames integration tests

- One Unit of Work and one repository instance read/write `tenant_a`, then `tenant_b`, then `tenant_a` again.
- Each schema sees only its own rows.
- Captured commands use fully qualified table names.
- Safe lowercase table identifiers may remain unquoted in Npgsql output (for example, `"tenant_a".orders`); schema qualification, not cosmetic table quoting, is the invariant.
- No search-path command is issued.
- A previously obtained IQueryable fails when executed under a different schema scope.
- Returning to the original schema reuses its schema-bound context safely.

### Raw SQL integration tests

- `FromSqlRaw` replaces `{{schema}}` and reads only the bound schema.
- `ExecuteSqlRaw` replaces `{{schema}}` and writes only the bound schema.
- Multiple tokens in joins/subqueries are replaced.
- SQL parameters remain parameters.
- Invalid schemas and unresolved placeholders fail before database access.
- Schema-independent raw SQL remains usable.

### Framework raw SQL tests

- Npgsql outbox lease, inbox lease, and background-job arming lease operations target the correct qualified schema.
- These operations issue no search-path commands in QualifiedNames mode.

### Domain event tests

- After non-transactional `SaveChangesAsync`, the outbox is empty.
- After `CommitAsync`, the outbox contains the event without any opt-in option.
- Events produced in `tenant_a` and `tenant_b` are written to their respective outboxes.
- Serialized CloudEvents retain the producing schema.
- Dispatcher/outbox failures propagate and do not mark the Unit of Work completed.
- Missing dispatcher with pending events fails explicitly.

### Nested scope tests

- Inner `Required.CommitAsync` does not commit the root early.
- Inner rollback aborts the root and prevents outer commit.
- Transactional inner `Required` plus non-transactional outer fails fast.
- `RequiresNew` remains isolated.

## Documentation

Update:

- `framework/docs/multi-schema/README.md`
- `framework/docs/multi-schema/IMPLEMENTATION_NOTES.md`
- `framework/docs/multi-schema/ADOPTION-GUIDE.md`
- `framework/docs/unit-of-work/README.md`
- `framework/docs/domain-events/README.md`

The documentation includes QualifiedNames registration, runtime repository switching, the `{{schema}}` raw-SQL contract, schema-bound object lifetime rules, non-transactional event timing, and the lack of business/outbox atomicity without a transaction.

## Out of Scope

- General PostgreSQL SQL parsing or automatic qualification of arbitrary unmarked raw SQL.
- Reusing one DbContext, DbSet, or IQueryable across different schema scopes.
- Making non-transactional business and outbox writes atomic.
- Changing SQL Server's single-schema limitation.
