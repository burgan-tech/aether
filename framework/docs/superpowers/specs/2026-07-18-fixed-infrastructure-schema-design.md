# Fixed Infrastructure Schema Design

## Goal

Keep application-level infrastructure persistence isolated from runtime business-schema switching.
Outbox, Inbox, and BackgroundJob each use the schema configured for their dedicated DbContext,
typically `sys_queues`. A request may change `ICurrentSchema` between tenant schemas, but those
changes must never redirect infrastructure rows to a tenant schema or mix infrastructure contexts.

This change completes the existing fixed-schema behavior for BackgroundJob. The Outbox and Inbox
stores already follow the same rule on this branch.

## Invariants

- `AetherOutboxOptions.Schema`, `AetherInboxOptions.Schema`, and `BackgroundJobOptions.Schema` are
  application configuration. They do not change at runtime.
- A configured infrastructure store resolves its dedicated DbContext under its configured schema on
  every operation.
- Caller-owned runtime `ICurrentSchema.Change(...)` scopes continue to select business schemas. A
  fixed-schema operation internally pushes its configured schema and restores the caller's schema
  before returning.
- The Unit of Work caches the resulting context by `(DbContextType, configured schema)`. Separate
  business and infrastructure DbContext types remain distinct while sharing the root Unit of Work's
  connection and transaction.
- A BackgroundJob payload retains the tenant schema active when the job was created. The job row is
  stored in the configured BackgroundJob schema; the payload schema is later used to execute the
  handler against the correct tenant.
- A null or blank infrastructure `Schema` preserves the historical tenant-local behavior.
- Existing public store constructor signatures remain available for source and binary compatibility.
- Migration schema selection remains model-driven. The dedicated infrastructure DbContext must retain
  its own configured/default schema for migration generation; runtime store options and scoping do not
  rewrite migration models.

## Considered Approaches

### Store-owned fixed schema scope (selected)

Each persistence boundary enters the immutable configured schema for the complete database operation:

- `EfCoreJobStore<TDbContext>` for all reads, writes, and conditional updates;
- `EfCoreJobArmingLeaseStore<TDbContext>` for the provider-agnostic claim path;
- `NpgsqlJobArmingLeaseStore<TDbContext>` for the PostgreSQL raw-SQL claim path.

This covers enqueue, dispatcher outcome updates, reapers, arming, and direct store consumers. It also
keeps the QualifiedNames command/current-schema guard valid for the entire query rather than only while
the DbContext is resolved.

### Service/processor-owned scope (rejected)

Changing only `BackgroundJobService` and `BackgroundJobArmingProcessor` leaves `JobDispatcher`, direct
`IJobStore` calls, and lease-store calls dependent on the caller's ambient schema. It cannot enforce the
invariant at the persistence boundary.

### Interceptor special-casing (rejected)

Teaching the PostgreSQL interceptor to recognize BackgroundJob tables couples application configuration
to a provider-specific SQL layer and does not cover provider-agnostic EF Core or SQL Server behavior.

## Runtime Data Flow

### Enqueue inside a tenant Unit of Work

1. The request runs under `tenant_a` and writes business data through its business DbContext.
2. `BackgroundJobService` creates the CloudEvent envelope while `tenant_a` is active, so
   `envelope.Schema == "tenant_a"`.
3. `EfCoreJobStore.SaveAsync` temporarily enters `BackgroundJobOptions.Schema`, for example
   `sys_queues`, and resolves the dedicated BackgroundJob DbContext.
4. The root Unit of Work caches `(BackgroundJobDbContext, sys_queues)` and stages the job row there.
5. The store scope restores `tenant_a`.
6. Commit saves both contexts on the shared connection and transaction. A rollback removes both writes.

### Dispatch and arming

- Dispatcher-controlled job-state reads and conditional updates enter the fixed BackgroundJob schema.
- Handler execution continues under the tenant schema carried by the stored envelope.
- EF and Npgsql lease stores enter the fixed schema before resolving the context and keep it active
  through query/raw-command execution.
- The existing arming processor's outer schema scope remains valid; store-level scopes nest safely and
  make direct calls equally safe.

## API and Dependency Injection

`AddAetherBackgroundJob<TDbContext>` already registers one immutable `BackgroundJobOptions` instance.
The DI-selected constructors receive that instance and `ICurrentSchema`.

Legacy public constructors delegate to ambient-schema behavior, matching their behavior before this
change. This prevents existing consumers, subclasses, and direct tests from breaking. The standard DI
path selects the longer constructor and enforces the configured schema.

## Error Handling

- Invalid configured schema names continue to fail through the existing schema formatter/identifier
  validation before database access.
- Nested schema scopes restore the previous runtime schema even when a query or update throws.
- QualifiedNames mismatch protection remains enabled because the fixed schema stays current for the
  complete database command.
- No payload, SQL text, or connection information is added to errors.

## Verification Strategy

Implementation follows a red-green TDD cycle.

### Unit tests

- Job store resolves its context while the configured BackgroundJob schema is current and restores the
  tenant schema after the operation.
- EF lease store does the same for claim queries and updates.
- Null-schema and legacy-constructor paths retain ambient tenant behavior.

### PostgreSQL QualifiedNames integration test

Use separate business and BackgroundJob DbContexts and separate schemas:

- write business data under `tenant_a`;
- enqueue a job while `tenant_a` is current;
- commit both writes in one transactional Unit of Work;
- assert business data exists only in `tenant_a`;
- assert `BackgroundJobs` exists and the row is written only in the configured job schema;
- assert the serialized job envelope still contains `Schema = tenant_a`;
- call job read/CAS and Npgsql lease operations while the caller is under `tenant_a`, and verify they
  target the configured job schema without a QualifiedNames mismatch;
- assert no `BackgroundJobs` relation is created in `tenant_a`;
- verify rollback removes both the business and job rows.

### Regression verification

- Build `framework/BBT.Aether.slnx`.
- Run the complete Infrastructure test project.
- Run the complete PostgreSQL test project serially.

## Documentation

Update `framework/docs/background-jobs/README.md` so `Schema` is described as the fixed persistence
schema for the dedicated BackgroundJob DbContext, not as a runtime tenant/poller schema. Document that
the payload independently carries the tenant schema used during handler execution.

## Out of Scope

- Changing migration generation or existing migrations.
- Sharing one infrastructure DbContext instance among Outbox, Inbox, and BackgroundJob.
- Making application-level infrastructure schemas mutable per request.
- Changing tenant resolution for business repositories or job handlers.
