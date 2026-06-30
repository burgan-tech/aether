using System;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Uow;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace BBT.Aether.Postgres.Tests;

[Collection("postgres")]
public sealed class MultiSchemaUnitOfWorkTests(PostgresFixture fx)
{
    // Unique schema pair per test instance avoids cross-test lock contention on the shared container.
    private readonly string _schemaA = "flow_a_" + Guid.NewGuid().ToString("N");
    private readonly string _schemaB = "flow_b_" + Guid.NewGuid().ToString("N");

    private async Task ArrangeSchemasAsync()
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();

        foreach (var schema in new[] { _schemaA, _schemaB })
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"""
                 CREATE SCHEMA {schema};
                 CREATE TABLE {schema}.things ("Id" uuid PRIMARY KEY, "Name" text NOT NULL);
                 """;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private IServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<BBT.Aether.Clock.IClock, BBT.Aether.Clock.SystemClock>();
        services.AddSingleton<IAetherDbContextConfigurator<ProbeDbContext>>(
            new AetherDbContextConfigurator<ProbeDbContext>(
                fx.ConnectionString,
                new NpgsqlAetherProvider(),
                configure: (_, _) => { },
                serviceProvider: null!));
        return services.BuildServiceProvider();
    }

    private async Task<long> CountAsync(string schema)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {schema}.things";
        var result = await cmd.ExecuteScalarAsync();
        return (long)result!;
    }

    [Fact]
    public async Task Commits_two_schemas_atomically()
    {
        await ArrangeSchemasAsync();
        var sp = BuildProvider();

        var uow = new CompositeUnitOfWork(sp);
        await uow.InitializeAsync(new UnitOfWorkOptions { IsTransactional = true });

        var a = await uow.GetDbContextAsync<ProbeDbContext>(_schemaA);
        a.Set<Thing>().Add(new Thing { Id = Guid.NewGuid(), Name = "a" });

        var b = await uow.GetDbContextAsync<ProbeDbContext>(_schemaB);
        b.Set<Thing>().Add(new Thing { Id = Guid.NewGuid(), Name = "b" });

        await uow.CommitAsync();
        await uow.DisposeAsync();

        (await CountAsync(_schemaA)).ShouldBe(1);
        (await CountAsync(_schemaB)).ShouldBe(1);
    }

    [Fact]
    public async Task Rolls_back_two_schemas_atomically()
    {
        await ArrangeSchemasAsync();
        var sp = BuildProvider();

        var uow = new CompositeUnitOfWork(sp);
        await uow.InitializeAsync(new UnitOfWorkOptions { IsTransactional = true });

        var a = await uow.GetDbContextAsync<ProbeDbContext>(_schemaA);
        a.Set<Thing>().Add(new Thing { Id = Guid.NewGuid(), Name = "a" });

        var b = await uow.GetDbContextAsync<ProbeDbContext>(_schemaB);
        b.Set<Thing>().Add(new Thing { Id = Guid.NewGuid(), Name = "b" });

        await uow.SaveChangesAsync();
        await uow.RollbackAsync();
        await uow.DisposeAsync();

        (await CountAsync(_schemaA)).ShouldBe(0);
        (await CountAsync(_schemaB)).ShouldBe(0);
    }

    [Fact]
    public async Task Exceeds_max_context_limit_throws()
    {
        await ArrangeSchemasAsync();
        var sp = BuildProvider();

        var uow = new CompositeUnitOfWork(sp);
        await uow.InitializeAsync(new UnitOfWorkOptions { IsTransactional = true, MaxDbContextCount = 1 });

        await uow.GetDbContextAsync<ProbeDbContext>(_schemaA);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            async () => await uow.GetDbContextAsync<ProbeDbContext>(_schemaB));
        ex.Message.ShouldContain("DbContext limit exceeded");

        await uow.DisposeAsync();
    }

    [Fact]
    public async Task Schema_isolation_via_search_path()
    {
        await ArrangeSchemasAsync();
        var sp = BuildProvider();

        var uow = new CompositeUnitOfWork(sp);
        await uow.InitializeAsync(new UnitOfWorkOptions { IsTransactional = true });

        var a = await uow.GetDbContextAsync<ProbeDbContext>(_schemaA);
        a.Set<Thing>().Add(new Thing { Id = Guid.NewGuid(), Name = "a1" });
        a.Set<Thing>().Add(new Thing { Id = Guid.NewGuid(), Name = "a2" });

        var b = await uow.GetDbContextAsync<ProbeDbContext>(_schemaB);
        b.Set<Thing>().Add(new Thing { Id = Guid.NewGuid(), Name = "b1" });

        await uow.SaveChangesAsync();

        // Each context only sees its own schema's rows via SET LOCAL search_path.
        (await a.Set<Thing>().CountAsync()).ShouldBe(2);
        (await b.Set<Thing>().CountAsync()).ShouldBe(1);

        await uow.CommitAsync();
        await uow.DisposeAsync();
    }

    [Fact]
    public async Task Same_schema_reads_stay_correct_after_cross_schema_switch()
    {
        // Proves the SET-skip optimization does not break correctness: consecutive same-schema
        // reads, and a flow_a read after touching flow_b, all resolve to the right schema.
        await ArrangeSchemasAsync();
        var sp = BuildProvider();

        var uow = new CompositeUnitOfWork(sp);
        await uow.InitializeAsync(new UnitOfWorkOptions { IsTransactional = true });

        var a = await uow.GetDbContextAsync<ProbeDbContext>(_schemaA);
        a.Set<Thing>().Add(new Thing { Id = Guid.NewGuid(), Name = "a1" });
        a.Set<Thing>().Add(new Thing { Id = Guid.NewGuid(), Name = "a2" });

        var b = await uow.GetDbContextAsync<ProbeDbContext>(_schemaB);
        b.Set<Thing>().Add(new Thing { Id = Guid.NewGuid(), Name = "b1" });

        await uow.SaveChangesAsync();

        // Two consecutive same-schema reads on flow_a (second one exercises the skip path).
        (await a.Set<Thing>().CountAsync()).ShouldBe(2);
        (await a.Set<Thing>().CountAsync()).ShouldBe(2);

        // Cross-schema switch to flow_b, then back to flow_a must re-apply flow_a's search_path.
        (await b.Set<Thing>().CountAsync()).ShouldBe(1);
        (await a.Set<Thing>().CountAsync()).ShouldBe(2);

        // And consecutive flow_b reads after the switch back also stay correct.
        (await b.Set<Thing>().CountAsync()).ShouldBe(1);
        (await b.Set<Thing>().CountAsync()).ShouldBe(1);

        await uow.CommitAsync();
        await uow.DisposeAsync();
    }

    [Fact]
    public async Task RollbackAsync_is_noop_after_completion()
    {
        await ArrangeSchemasAsync();
        var sp = BuildProvider();

        var failedHandlerCalls = 0;
        var uow = new CompositeUnitOfWork(sp);
        await uow.InitializeAsync(new UnitOfWorkOptions { IsTransactional = true });
        uow.OnFailed((_, _) => { failedHandlerCalls++; return Task.CompletedTask; });

        // Materialize a context so a connection/transaction exist.
        _ = await uow.GetDbContextAsync<ProbeDbContext>(_schemaA);

        await uow.CommitAsync();

        await uow.RollbackAsync(); // must be a no-op

        failedHandlerCalls.ShouldBe(0);
        uow.IsCompleted.ShouldBeTrue();

        await uow.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_sets_disposed_and_is_idempotent()
    {
        await ArrangeSchemasAsync();
        var sp = BuildProvider();

        var uow = new CompositeUnitOfWork(sp);
        await uow.InitializeAsync(new UnitOfWorkOptions { IsTransactional = true });

        // Materialize a context so a connection/transaction exist.
        _ = await uow.GetDbContextAsync<ProbeDbContext>(_schemaA);

        await uow.DisposeAsync();

        uow.IsDisposed.ShouldBeTrue();

        await uow.DisposeAsync(); // second dispose must be a no-op (no throw)
    }

    [Fact]
    public async Task Failed_handlers_fire_at_most_once_across_rollback_and_dispose()
    {
        await ArrangeSchemasAsync();
        var sp = BuildProvider();

        var failedHandlerCalls = 0;
        var uow = new CompositeUnitOfWork(sp);
        await uow.InitializeAsync(new UnitOfWorkOptions { IsTransactional = true });
        uow.OnFailed((_, _) => { failedHandlerCalls++; return Task.CompletedTask; });

        // Force the commit to throw at SaveChanges: seed a row via raw SQL (same transaction; search_path
        // resolves the unqualified table to this context's schema), then add an EF row with the same PK so
        // the commit-time INSERT hits a unique violation. (Adding two tracked entities with the same key
        // would instead throw at tracking time, before commit.)
        var a = await uow.GetDbContextAsync<ProbeDbContext>(_schemaA);
        var dupId = Guid.NewGuid();
        await a.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO things (\"Id\", \"Name\") VALUES ({dupId}, 'seed')");
        a.Set<Thing>().Add(new Thing { Id = dupId, Name = "dup" });

        await Should.ThrowAsync<Exception>(async () => await uow.CommitAsync()); // sets _exception, rethrows

        // Explicit rollback (fires failed handlers once), then dispose (must NOT fire them again).
        await uow.RollbackAsync();
        await uow.DisposeAsync();

        failedHandlerCalls.ShouldBe(1);
    }

    [Fact]
    public async Task TransactionLocal_read_without_IsTransactional_succeeds()
    {
        // Regression: TransactionLocal mode with IsTransactional = false used to throw
        // "SchemaSwitchingMode.TransactionLocal requires a transaction, but none is active."
        // for read-only operations. NpgsqlAetherProvider.RequiresTransaction now signals
        // CompositeUnitOfWork to open a transaction automatically.
        await ArrangeSchemasAsync();
        var sp = BuildProvider(); // default NpgsqlAetherProvider → TransactionLocal

        var uow = new CompositeUnitOfWork(sp);
        await uow.InitializeAsync(new UnitOfWorkOptions { IsTransactional = false });

        var a = await uow.GetDbContextAsync<ProbeDbContext>(_schemaA);
        var count = await a.Set<Thing>().CountAsync();
        count.ShouldBe(0);

        await uow.DisposeAsync();
    }

    [Fact]
    public async Task Required_participant_work_is_committed_by_the_owner()
    {
        await ArrangeSchemasAsync();
        var mgr = new UnitOfWorkManager(new AsyncLocalAmbientUowAccessor(), BuildProvider());

        await using var owner = mgr.Begin(new UnitOfWorkOptions { IsTransactional = true });

        var db1 = await ((IEfCoreUnitOfWork)mgr.Current!).GetDbContextAsync<ProbeDbContext>(_schemaA);
        db1.Set<Thing>().Add(new Thing { Id = Guid.NewGuid(), Name = "outer" });

        // Nested Required participant: shares the owner's root, does NOT own it, must NOT commit.
        await using (var participant = mgr.Begin(new UnitOfWorkOptions
                     {
                         IsTransactional = true, Scope = UnitOfWorkScopeOption.Required
                     }))
        {
            var db2 = await ((IEfCoreUnitOfWork)mgr.Current!).GetDbContextAsync<ProbeDbContext>(_schemaA);
            db2.Set<Thing>().Add(new Thing { Id = Guid.NewGuid(), Name = "inner" });
            // intentionally no CommitAsync — the owner commits everything.
        }

        await owner.CommitAsync();

        (await CountAsync(_schemaA)).ShouldBe(2);
    }

    [Fact]
    public async Task RequiresNew_commits_independently_of_a_rolled_back_outer()
    {
        await ArrangeSchemasAsync();
        var mgr = new UnitOfWorkManager(new AsyncLocalAmbientUowAccessor(), BuildProvider());

        await using var outer = mgr.Begin(new UnitOfWorkOptions { IsTransactional = true });
        var outerDb = await ((IEfCoreUnitOfWork)mgr.Current!).GetDbContextAsync<ProbeDbContext>(_schemaA);
        outerDb.Set<Thing>().Add(new Thing { Id = Guid.NewGuid(), Name = "outer" });

        await using (var inner = mgr.Begin(new UnitOfWorkOptions
                     {
                         IsTransactional = true, Scope = UnitOfWorkScopeOption.RequiresNew
                     }))
        {
            var innerDb = await ((IEfCoreUnitOfWork)mgr.Current!).GetDbContextAsync<ProbeDbContext>(_schemaB);
            innerDb.Set<Thing>().Add(new Thing { Id = Guid.NewGuid(), Name = "inner" });
            await inner.CommitAsync();
        }

        await outer.RollbackAsync();

        (await CountAsync(_schemaB)).ShouldBe(1);
        (await CountAsync(_schemaA)).ShouldBe(0);
    }

    public sealed class Thing
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options)
        : AetherDbContext<ProbeDbContext>(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Thing>().ToTable("things");
        }
    }
}
