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
