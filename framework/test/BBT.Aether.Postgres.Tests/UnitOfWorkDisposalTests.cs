using System;
using System.Data;
using System.Data.Common;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.Domain.Entities;
using BBT.Aether.MultiSchema;
using BBT.Aether.Persistence;
using BBT.Aether.Uow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace BBT.Aether.Postgres.Tests;

/// <summary>
/// Regression coverage for the manager-path root disposal (the gap that hid the shared-connection
/// leak): when a unit of work is begun through <see cref="IUnitOfWorkManager"/> (NOT by constructing
/// <see cref="CompositeUnitOfWork"/> directly), disposing the OWNING <see cref="UnitOfWorkScope"/>
/// must dispose the root and release the shared <see cref="NpgsqlConnection"/>. A participating
/// <c>Required</c> scope disposing must NOT close the shared connection.
/// </summary>
[Collection("postgres")]
public sealed class UnitOfWorkDisposalTests(PostgresFixture fx)
{
    private readonly string _schema = "flow_disposal_" + Guid.NewGuid().ToString("N");

    private sealed class Thing : AggregateRoot<Guid>
    {
        private Thing() { }

        public Thing(Guid id, string name) : base(id)
        {
            Name = name;
        }

        public string Name { get; private set; } = string.Empty;
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options)
        : AetherDbContext<TestDbContext>(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Thing>(e =>
            {
                e.ToTable("things"); // NO schema - resolved at runtime via SET LOCAL search_path
                e.HasKey(t => t.Id);
                e.Property(t => t.Name).IsRequired();
            });
        }
    }

    private IServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();

        // Core: IClock, IGuidGenerator, ICurrentSchema, ISchemaNameFormatter, etc.
        services.AddAetherCore(_ => { });

        // DbContext + UnitOfWork wiring (configurator, UoW manager, ambient accessor, provider).
        services.AddAetherDbContext<TestDbContext>(
            fx.ConnectionString,
            (_, b) => b.UseNpgsql(fx.ConnectionString));

        return services.BuildServiceProvider();
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

    [Fact]
    public async Task Owner_scope_dispose_disposes_root_and_releases_connection()
    {
        await ArrangeSchemaAsync();
        var sp = BuildProvider();

        await using var scope = sp.CreateAsyncScope();
        var ssp = scope.ServiceProvider;
        var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
        var mgr = ssp.GetRequiredService<IUnitOfWorkManager>();
        var provider = ssp.GetRequiredService<IAetherDbContextProvider<TestDbContext>>();

        DbConnection? conn;

        using (currentSchema.Change(_schema))
        {
            // RequiresNew -> this scope is the OWNER of a freshly-created root.
            var uow = mgr.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

            // Materialize a DbContext on the root, which opens the shared NpgsqlConnection + transaction.
            var db = await provider.GetDbContextAsync();
            conn = db.Database.GetDbConnection();
            conn.State.ShouldBe(ConnectionState.Open);

            await uow.CommitAsync();
            await uow.DisposeAsync();
        }

        // After the owning scope is disposed, the root (and its shared connection) must be torn down.
        // A disposed NpgsqlConnection reports Closed. Before the ownership fix this stayed Open (leak).
        conn.State.ShouldBe(ConnectionState.Closed);
    }

    [Fact]
    public async Task Required_nested_scope_dispose_does_not_close_shared_connection()
    {
        await ArrangeSchemaAsync();
        var sp = BuildProvider();

        await using var scope = sp.CreateAsyncScope();
        var ssp = scope.ServiceProvider;
        var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
        var mgr = ssp.GetRequiredService<IUnitOfWorkManager>();
        var provider = ssp.GetRequiredService<IAetherDbContextProvider<TestDbContext>>();

        DbConnection? conn;

        using (currentSchema.Change(_schema))
        {
            // Owner UoW (RequiresNew) creates the root.
            var outer = mgr.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

            var db = await provider.GetDbContextAsync();
            conn = db.Database.GetDbConnection();
            conn.State.ShouldBe(ConnectionState.Open);

            // Inner Required scope participates in the SAME root (does NOT own it).
            var inner = mgr.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.Required, IsTransactional = true });

            // Disposing the non-owner inner scope must only restore ambient, NOT close the shared connection.
            await inner.DisposeAsync();
            conn.State.ShouldBe(ConnectionState.Open);

            // Disposing the owner tears the root (and connection) down.
            await outer.DisposeAsync();
        }

        conn.State.ShouldBe(ConnectionState.Closed);
    }
}
