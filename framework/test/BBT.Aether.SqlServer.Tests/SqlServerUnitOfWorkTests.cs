using System;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.MultiSchema;
using BBT.Aether.Uow;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace BBT.Aether.SqlServer.Tests;

/// <summary>
/// Integration coverage proving the Aether UnitOfWork commits and rolls back correctly on SQL
/// Server through the <c>BBT.Aether.SqlServer</c> provider. SQL Server is SINGLE-SCHEMA: the
/// schema ("app") is bound in the model via <c>HasDefaultSchema</c>; the provider supplies the
/// shared connection/transaction but never switches schema per command. This validates the
/// provider abstraction holds for a second engine.
/// <para>
/// Each test soft-skips when <c>AETHER_SKIP_MSSQL=1</c> (see <see cref="SqlServerFixture"/>) so a
/// CI host that cannot pull the MsSql image stays green.
/// </para>
/// </summary>
[Collection("sqlserver")]
public sealed class SqlServerUnitOfWorkTests(SqlServerFixture fx)
{
    private const string Schema = "app";

    public sealed class Thing
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options)
        : AetherDbContext<TestDbContext>(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.HasDefaultSchema(Schema);
            modelBuilder.Entity<Thing>(e =>
            {
                e.ToTable("Things");
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
        services.AddAetherSqlServer<TestDbContext>(fx.ConnectionString);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Creates the <c>app</c> schema and <c>app.Things</c> table via EF Core's own
    /// <c>GenerateCreateScript()</c>. Because the model declares <c>HasDefaultSchema("app")</c>,
    /// the generated script both creates the schema and qualifies the table with <c>app</c>, so
    /// it matches the entity shape exactly. Idempotent across reruns: drops table/schema first.
    /// </summary>
    private async Task ArrangeSchemaAsync(IServiceProvider sp)
    {
        var configurator = sp.GetRequiredService<IAetherDbContextConfigurator<TestDbContext>>();

        await using var modelConn = new SqlConnection(fx.ConnectionString);
        await modelConn.OpenAsync();
        await using var ctx = ActivatorUtilities.CreateInstance<TestDbContext>(
            sp, configurator.BuildOptions(modelConn, Schema, new SchemaScopeState()));
        var script = ctx.Database.GenerateCreateScript();

        await using var conn = new SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();

        // Clean slate so reruns against a reused container are deterministic.
        await using (var dropCmd = conn.CreateCommand())
        {
            dropCmd.CommandText =
                $"""
                 IF OBJECT_ID(N'[{Schema}].[Things]', N'U') IS NOT NULL DROP TABLE [{Schema}].[Things];
                 IF EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'{Schema}') DROP SCHEMA [{Schema}];
                 """;
            await dropCmd.ExecuteNonQueryAsync();
        }

        // EF emits batches separated by GO; SqlConnection cannot run GO, so split on it.
        foreach (var batch in script.Split(["\r\nGO\r\n", "\nGO\n", "\nGO"], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = batch.Trim();
            if (trimmed.Length == 0) continue;
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = trimmed;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task<int> CountAsync(Guid id)
    {
        await using var conn = new SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM [{Schema}].[Things] WHERE Id = @id";
        var p = cmd.CreateParameter();
        p.ParameterName = "@id";
        p.Value = id;
        cmd.Parameters.Add(p);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Commits_within_transaction()
    {
        if (SqlServerFixture.Skip) return;

        var sp = BuildProvider();
        await ArrangeSchemaAsync(sp);

        await using var scope = sp.CreateAsyncScope();
        var ssp = scope.ServiceProvider;
        var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
        var mgr = ssp.GetRequiredService<IUnitOfWorkManager>();
        var provider = ssp.GetRequiredService<IAetherDbContextProvider<TestDbContext>>();

        var id = Guid.NewGuid();

        using (currentSchema.Change(Schema))
        {
            await using var uow = mgr.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

            var db = await provider.GetDbContextAsync();
            db.Set<Thing>().Add(new Thing { Id = id, Name = "x" });

            await uow.CommitAsync();
        }

        (await CountAsync(id)).ShouldBe(1);
    }

    [Fact]
    public async Task Rolls_back_within_transaction()
    {
        if (SqlServerFixture.Skip) return;

        var sp = BuildProvider();
        await ArrangeSchemaAsync(sp);

        await using var scope = sp.CreateAsyncScope();
        var ssp = scope.ServiceProvider;
        var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
        var mgr = ssp.GetRequiredService<IUnitOfWorkManager>();
        var provider = ssp.GetRequiredService<IAetherDbContextProvider<TestDbContext>>();

        var id = Guid.NewGuid();

        using (currentSchema.Change(Schema))
        {
            await using var uow = mgr.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

            var db = await provider.GetDbContextAsync();
            db.Set<Thing>().Add(new Thing { Id = id, Name = "x" });

            // Flush into the transaction so a real rollback has something to discard.
            await uow.SaveChangesAsync();

            await uow.RollbackAsync();
        }

        (await CountAsync(id)).ShouldBe(0);
    }
}
