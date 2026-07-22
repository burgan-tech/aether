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
using BBT.Aether.Uow.EntityFrameworkCore;
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
                e.ToTable("things"); // NO schema - rewritten at runtime to the qualified schema name
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
        services.AddAetherNpgsql<TestDbContext>(fx.ConnectionString);

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
    public async Task Begin_exposes_options_through_Current()
    {
        var sp = BuildProvider();
        var mgr = sp.GetRequiredService<IUnitOfWorkManager>();

        var opts = new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true };
        await using var uow = mgr.Begin(opts);

        mgr.Current.ShouldNotBeNull();
        mgr.Current!.Options.ShouldNotBeNull();
        mgr.Current.Options!.IsTransactional.ShouldBeTrue();
    }

    [Fact]
    public async Task Required_nested_scope_commit_and_dispose_do_not_complete_root_or_close_shared_connection()
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

            // Committing a participating scope is logical-only: the owning root and its connection stay active.
            await inner.CommitAsync();
            inner.IsCompleted.ShouldBeTrue();
            outer.IsCompleted.ShouldBeFalse();
            conn.State.ShouldBe(ConnectionState.Open);

            // Disposing the non-owner inner scope must only restore ambient, NOT close the shared connection.
            await inner.DisposeAsync();
            conn.State.ShouldBe(ConnectionState.Open);

            // Disposing the owner tears the root (and connection) down.
            await outer.DisposeAsync();
        }

        conn.State.ShouldBe(ConnectionState.Closed);
    }

    [Fact]
    public async Task NonTransactional_context_leaves_connection_management_to_ef_core()
    {
        await ArrangeSchemaAsync();
        var sp = BuildProvider();

        await using var scope = sp.CreateAsyncScope();
        var ssp = scope.ServiceProvider;
        var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
        var mgr = ssp.GetRequiredService<IUnitOfWorkManager>();
        var provider = ssp.GetRequiredService<IAetherDbContextProvider<TestDbContext>>();

        using (currentSchema.Change(_schema))
        {
            await using var uow = mgr.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = false });

            var db = await provider.GetDbContextAsync();

            // Requesting the context must NOT open a physical connection: EF Core rents one
            // per operation and returns it to the pool immediately afterwards.
            db.Database.GetDbConnection().State.ShouldBe(ConnectionState.Closed);
            db.Database.CurrentTransaction.ShouldBeNull();

            (await db.Set<Thing>().CountAsync()).ShouldBe(0);
            db.Database.GetDbConnection().State.ShouldBe(ConnectionState.Closed);

            await uow.CommitAsync();
        }
    }

    [Fact]
    public async Task Connection_uses_transaction_mode_captured_at_begin_after_input_options_mutate()
    {
        await ArrangeSchemaAsync();
        var sp = BuildProvider();

        await using var scope = sp.CreateAsyncScope();
        var ssp = scope.ServiceProvider;
        var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
        var mgr = ssp.GetRequiredService<IUnitOfWorkManager>();
        var provider = ssp.GetRequiredService<IAetherDbContextProvider<TestDbContext>>();

        using (currentSchema.Change(_schema))
        {
            var options = new UnitOfWorkOptions
            {
                Scope = UnitOfWorkScopeOption.RequiresNew,
                IsTransactional = true
            };
            await using var uow = mgr.Begin(options);

            options.IsTransactional = false;
            uow.Options!.IsTransactional = false;
            var db = await provider.GetDbContextAsync();

            db.Database.CurrentTransaction.ShouldNotBeNull();
            await uow.CommitAsync();
        }
    }

    [Fact]
    public async Task NonTransactional_same_schema_reuses_the_same_context()
    {
        await ArrangeSchemaAsync();
        var sp = BuildProvider();

        await using var scope = sp.CreateAsyncScope();
        var ssp = scope.ServiceProvider;
        var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
        var mgr = ssp.GetRequiredService<IUnitOfWorkManager>();
        var provider = ssp.GetRequiredService<IAetherDbContextProvider<TestDbContext>>();

        using (currentSchema.Change(_schema))
        {
            await using var uow = mgr.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = false });

            var db1 = await provider.GetDbContextAsync();
            var db2 = await provider.GetDbContextAsync();

            db2.ShouldBeSameAs(db1);
            db2.Database.GetDbConnection().ShouldBeSameAs(db1.Database.GetDbConnection());

            await uow.CommitAsync();
        }
    }

    [Fact]
    public async Task Transactional_opens_shared_connection_and_transaction()
    {
        await ArrangeSchemaAsync();
        var sp = BuildProvider();

        await using var scope = sp.CreateAsyncScope();
        var ssp = scope.ServiceProvider;
        var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
        var mgr = ssp.GetRequiredService<IUnitOfWorkManager>();
        var provider = ssp.GetRequiredService<IAetherDbContextProvider<TestDbContext>>();

        using (currentSchema.Change(_schema))
        {
            await using var uow = mgr.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

            var db = await provider.GetDbContextAsync();

            db.Database.GetDbConnection().State.ShouldBe(ConnectionState.Open);
            db.Database.CurrentTransaction.ShouldNotBeNull();

            await uow.CommitAsync();
        }
    }

    [Fact]
    public async Task Schema_does_not_leak_across_units_of_work()
    {
        var schemaA = "leak_a_" + Guid.NewGuid().ToString("N");
        var schemaB = "leak_b_" + Guid.NewGuid().ToString("N");

        await using (var setupConn = new NpgsqlConnection(fx.ConnectionString))
        {
            await setupConn.OpenAsync();
            await using var cmd = setupConn.CreateCommand();
            cmd.CommandText =
                $"""
                 CREATE SCHEMA "{schemaA}";
                 CREATE TABLE "{schemaA}".things ("Id" uuid PRIMARY KEY, "Name" text NOT NULL);
                 INSERT INTO "{schemaA}".things VALUES (gen_random_uuid(), 'from-a');
                 CREATE SCHEMA "{schemaB}";
                 CREATE TABLE "{schemaB}".things ("Id" uuid PRIMARY KEY, "Name" text NOT NULL);
                 """;
            await cmd.ExecuteNonQueryAsync();
        }

        var sp = BuildProvider();
        await using var scope = sp.CreateAsyncScope();
        var ssp = scope.ServiceProvider;
        var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
        var mgr = ssp.GetRequiredService<IUnitOfWorkManager>();
        var provider = ssp.GetRequiredService<IAetherDbContextProvider<TestDbContext>>();

        using (currentSchema.Change(schemaA))
        {
            var uowA = mgr.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = false });
            var dbA = await provider.GetDbContextAsync();
            (await dbA.Set<Thing>().CountAsync()).ShouldBe(1);
            await uowA.CommitAsync();
            await uowA.DisposeAsync();
        }

        using (currentSchema.Change(schemaB))
        {
            await using var uowB = mgr.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = false });
            var dbB = await provider.GetDbContextAsync();
            (await dbB.Set<Thing>().CountAsync())
                .ShouldBe(0, "schema binding from the previous UoW must not leak into this one");
            await uowB.CommitAsync();
        }
    }
}
