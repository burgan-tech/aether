using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.MultiSchema;
using BBT.Aether.Uow;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace BBT.Aether.Postgres.Tests;

[Collection("postgres")]
public sealed class QualifiedNamesTests(PostgresFixture fx)
{
    private readonly string _schemaA = "qualified___aether_schema___" + Guid.NewGuid().ToString("N");
    private readonly string _schemaB = "qualified_b_" + Guid.NewGuid().ToString("N");
    private readonly CommandCaptureInterceptor _commands = new();

    private ServiceProvider BuildProvider(PostgresFixture fixture)
    {
        var services = new ServiceCollection();
        services.AddAetherCore(_ => { });
        services.AddAetherNpgsql<TestDbContext>(
            fixture.ConnectionString,
            SchemaSwitchingMode.QualifiedNames,
            (_, builder) => builder.AddInterceptors(_commands));
        services.AddScoped<IEfCoreRepository<Thing, Guid>>(sp =>
            new EfCoreRepository<TestDbContext, Thing, Guid>(
                sp.GetRequiredService<IAetherDbContextProvider<TestDbContext>>(), sp));
        return services.BuildServiceProvider();
    }

    private async Task ArrangeSchemasAsync()
    {
        await using var connection = new NpgsqlConnection(fx.ConnectionString);
        await connection.OpenAsync();

        foreach (var schema in new[] { _schemaA, _schemaB })
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                 CREATE SCHEMA "{schema}";
                 CREATE TABLE "{schema}".things ("Id" uuid PRIMARY KEY, "Name" text NOT NULL);
                 """;
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Same_repository_switches_tenant_a_to_tenant_b_and_back()
    {
        await ArrangeSchemasAsync();
        using var provider = BuildProvider(fx);
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var currentSchema = sp.GetRequiredService<ICurrentSchema>();
        var manager = sp.GetRequiredService<IUnitOfWorkManager>();
        var repository = sp.GetRequiredService<IEfCoreRepository<Thing, Guid>>();

        await using var uow = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = false
        });

        using (currentSchema.Change(_schemaA))
            await repository.InsertAsync(new Thing(Guid.NewGuid(), "a"), true);
        using (currentSchema.Change(_schemaB))
            await repository.InsertAsync(new Thing(Guid.NewGuid(), "b"), true);
        using (currentSchema.Change(_schemaA))
            (await repository.GetListAsync()).Select(x => x.Name).ShouldBe(["a"]);
        using (currentSchema.Change(_schemaB))
            (await repository.GetListAsync()).Select(x => x.Name).ShouldBe(["b"]);

        IQueryable<Thing> query;
        using (currentSchema.Change(_schemaA))
            query = await repository.GetQueryableAsync();

        var commandCount = _commands.CommandTexts.Count;
        using (currentSchema.Change(_schemaB))
        {
            var exception = await Should.ThrowAsync<InvalidOperationException>(query.ToListAsync());
            exception.Message.ShouldBe(
                $"DbContext is bound to schema '{_schemaA}', but current schema is '{_schemaB}'. " +
                "Resolve the DbContext again inside the new schema scope.");
        }
        _commands.CommandTexts.Count.ShouldBe(commandCount);

        await uow.CommitAsync();

        _commands.CommandTexts.ShouldContain(text => text.Contains($"\"{_schemaA}\".things", StringComparison.Ordinal));
        _commands.CommandTexts.ShouldContain(text => text.Contains($"\"{_schemaB}\".things", StringComparison.Ordinal));
        _commands.CommandTexts.ShouldAllBe(text =>
            !text.Contains("search_path", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Quoted_placeholder_rewrite_does_not_rewrite_schema_contents()
    {
        const string schema = "tenant___aether_schema___archive";
        var interceptor = new SearchPathCommandInterceptor(
            schema,
            new SchemaScopeState(),
            SchemaSwitchingMode.QualifiedNames,
            new StaticCurrentSchema(schema));
        using var command = new NpgsqlCommand(
            "SELECT * FROM \"__aether_schema__\".\"things\"");

        interceptor.ReaderExecuting(command, null!, default);

        command.CommandText.ShouldBe(
            "SELECT * FROM \"tenant___aether_schema___archive\".\"things\"");
    }

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
            modelBuilder.Entity<Thing>(entity =>
            {
                entity.ToTable("things");
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Name).IsRequired();
            });
        }
    }

    private sealed class CommandCaptureInterceptor : DbCommandInterceptor
    {
        public List<string> CommandTexts { get; } = [];

        public override DbDataReader ReaderExecuted(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result)
        {
            CommandTexts.Add(command.CommandText);
            return result;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            CommandTexts.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
        {
            CommandTexts.Add(command.CommandText);
            return result;
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            CommandTexts.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
