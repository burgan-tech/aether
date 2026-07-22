using System;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.MultiSchema;
using BBT.Aether.Uow;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace BBT.Aether.Postgres.Tests;

/// <summary>
/// Proves the core PgBouncer-safety guarantee: qualified-names schema targeting never touches
/// the connection's <c>search_path</c>, so no schema state can leak into a connection later
/// handed to another request. The assertion is about the ABSENCE of the leak regardless of
/// whether Npgsql hands back the same physical connection.
/// </summary>
[Collection("postgres")]
public sealed class PgBouncerSearchPathTests(PostgresFixture fx)
{
    private readonly string _schema = "flow_x_" + Guid.NewGuid().ToString("N");

    private sealed class Thing
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options)
        : AetherDbContext<ProbeDbContext>(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Thing>().ToTable("things");
        }
    }

    private async Task ArrangeSchemaAsync()
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"""
             CREATE SCHEMA "{_schema}";
             CREATE TABLE "{_schema}".things ("Id" uuid PRIMARY KEY, "Name" text NOT NULL);
             """;
        await cmd.ExecuteNonQueryAsync();
    }

    private IServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ICurrentSchema>(new StaticCurrentSchema(_schema));
        services.AddSingleton<IAetherDbContextConfigurator<ProbeDbContext>>(sp =>
            new AetherDbContextConfigurator<ProbeDbContext>(
                fx.ConnectionString,
                new NpgsqlAetherProvider(),
                configure: (_, _) => { },
                serviceProvider: sp));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Schema_state_does_not_leak_to_a_fresh_connection()
    {
        await ArrangeSchemaAsync();
        var sp = BuildProvider();

        // Run a full UoW that materializes a context for the test schema and executes commands,
        // so the qualified-names rewrite actually runs inside the transaction.
        var uow = new CompositeUnitOfWork(sp);
        await uow.InitializeAsync(new UnitOfWorkOptions { IsTransactional = true });

        var ctx = await uow.GetDbContextAsync<ProbeDbContext>(_schema);
        ctx.Set<Thing>().Add(new Thing { Id = Guid.NewGuid(), Name = "x" });
        await uow.SaveChangesAsync();
        (await ctx.Set<Thing>().CountAsync()).ShouldBe(1); // forces a read through the interceptor

        await uow.CommitAsync();
        await uow.DisposeAsync(); // returns the connection to the pool

        // Open a BRAND NEW connection from the same connection string (simulating a pooled
        // connection handed to another request) and inspect its session search_path.
        await using var fresh = new NpgsqlConnection(fx.ConnectionString);
        await fresh.OpenAsync();
        await using var cmd = fresh.CreateCommand();
        cmd.CommandText = "SHOW search_path";
        var searchPath = (string)(await cmd.ExecuteScalarAsync())!;

        // Qualified names never mutate session/pooled search_path state.
        searchPath.ShouldNotContain(_schema);
    }
}
