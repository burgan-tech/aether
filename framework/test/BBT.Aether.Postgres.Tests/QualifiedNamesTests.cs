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

    [Fact]
    public void Raw_SQL_tokens_are_rewritten_only_in_SQL_code_regions()
    {
        const string schema = "tenant";
        var interceptor = new SearchPathCommandInterceptor(
            schema,
            new SchemaScopeState(),
            SchemaSwitchingMode.QualifiedNames,
            new StaticCurrentSchema(schema));
        using var command = new NpgsqlCommand(
            """
            SELECT '{schema}', '{{schema}}', 'it''s {schema}', E'it\'s {{schema}}'
            , "{schema}", "{{schema}}"
            -- {schema} {{schema}}
            /* {schema} /* nested {{schema}} */ {schema} */
            , $$ BEGIN RAISE NOTICE '{schema}'; END $$
            , $body$ SELECT '{{schema}}'; $body$
            FROM {schema}."things" AS formatted
            JOIN {{schema}}."things" AS public ON public."Id" = formatted."Id"
            """);

        interceptor.ReaderExecuting(command, null!, default);

        command.CommandText.ShouldBe(
            """
            SELECT '{schema}', '{{schema}}', 'it''s {schema}', E'it\'s {{schema}}'
            , "{schema}", "{{schema}}"
            -- {schema} {{schema}}
            /* {schema} /* nested {{schema}} */ {schema} */
            , $$ BEGIN RAISE NOTICE '{schema}'; END $$
            , $body$ SELECT '{{schema}}'; $body$
            FROM "tenant"."things" AS formatted
            JOIN "tenant"."things" AS public ON public."Id" = formatted."Id"
            """);
    }

    [Fact]
    public void Raw_SQL_token_after_ordinary_string_ending_in_backslash_is_rewritten()
    {
        const string schema = "tenant";
        var interceptor = new SearchPathCommandInterceptor(
            schema,
            new SchemaScopeState(),
            SchemaSwitchingMode.QualifiedNames,
            new StaticCurrentSchema(schema));
        using var command = new NpgsqlCommand(
            """
            SELECT 'ordinary\', {schema}."things"
            """);

        interceptor.ReaderExecuting(command, null!, default);

        command.CommandText.ShouldBe(
            """
            SELECT 'ordinary\', "tenant"."things"
            """);
    }

    [Fact]
    public void Raw_SQL_tokens_in_escape_string_with_escaped_quote_and_backslash_are_protected()
    {
        const string schema = "tenant";
        var interceptor = new SearchPathCommandInterceptor(
            schema,
            new SchemaScopeState(),
            SchemaSwitchingMode.QualifiedNames,
            new StaticCurrentSchema(schema));
        using var command = new NpgsqlCommand(
            """
            SELECT E'escaped quote \'{schema} and backslash \\{{schema}}'
            , e'escaped quote \'{{schema}} and backslash \\{schema}'
            , {schema}."things"
            """);

        interceptor.ReaderExecuting(command, null!, default);

        command.CommandText.ShouldBe(
            """
            SELECT E'escaped quote \'{schema} and backslash \\{{schema}}'
            , e'escaped quote \'{{schema}} and backslash \\{schema}'
            , "tenant"."things"
            """);
    }

    [Fact]
    public void Raw_SQL_tokens_in_protected_regions_are_not_rejected_outside_qualified_names_mode()
    {
        const string schema = "tenant";
        var state = new SchemaScopeState { Current = schema };
        var interceptor = new SearchPathCommandInterceptor(
            schema,
            state,
            SchemaSwitchingMode.SessionSearchPath,
            new StaticCurrentSchema(schema));
        using var command = new NpgsqlCommand(
            """
            SELECT '{schema}', "{{schema}}"
            -- {schema}
            /* outer {{schema}} /* nested {schema} */ */
            , $$ SELECT '{schema}' $$, $tag$ {{schema}} $tag$
            """);

        Should.NotThrow(() => interceptor.ReaderExecuting(command, null!, default));
    }

    [Fact]
    public async Task Raw_SQL_token_supports_queries_updates_repeated_tokens_and_parameters()
    {
        await ArrangeSchemasAsync();
        using var provider = BuildProvider(fx);
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var currentSchema = sp.GetRequiredService<ICurrentSchema>();
        var manager = sp.GetRequiredService<IUnitOfWorkManager>();
        var repository = sp.GetRequiredService<IEfCoreRepository<Thing, Guid>>();
        var dbContextProvider = sp.GetRequiredService<IAetherDbContextProvider<TestDbContext>>();

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
        {
            var db = await dbContextProvider.GetDbContextAsync();
            var nameParameter = new NpgsqlParameter("name", "a");
            var rows = await db.Set<Thing>()
                .FromSqlRaw(
                    "SELECT * FROM {{schema}}.\"things\" WHERE \"Name\" = @name",
                    nameParameter)
                .ToListAsync();

            rows.Select(x => x.Name).ShouldBe(["a"]);
            nameParameter.ParameterName.ShouldBe("name");
            nameParameter.Value.ShouldBe("a");

            await db.Database.ExecuteSqlRawAsync(
                "UPDATE {{schema}}.\"things\" SET \"Name\" = {0} WHERE \"Name\" = {1}",
                "updated", "a");

            var repeatedTokenRows = await db.Set<Thing>()
                .FromSqlRaw(
                    """
                    SELECT source.*
                    FROM {{schema}}."things" AS source
                    WHERE EXISTS (
                        SELECT 1 FROM {{schema}}."things" AS candidate
                        WHERE candidate."Id" = source."Id" AND candidate."Name" = {0})
                    """,
                    "updated")
                .AsNoTracking()
                .ToListAsync();
            repeatedTokenRows.Select(x => x.Name).ShouldBe(["updated"]);

            var scalar = await db.Database
                .SqlQueryRaw<int>("SELECT 1 AS \"Value\"")
                .SingleAsync();
            scalar.ShouldBe(1);
        }

        using (currentSchema.Change(_schemaB))
            (await repository.GetListAsync()).Select(x => x.Name).ShouldBe(["b"]);

        await uow.CommitAsync();
    }

    [Theory]
    [InlineData("invalid schema")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Raw_SQL_invalid_runtime_schema_is_rejected_before_database_access(string schema)
    {
        var commandCount = _commands.CommandTexts.Count;

        var exception = Should.Throw<Exception>(() => new SearchPathCommandInterceptor(
            schema,
            new SchemaScopeState(),
            SchemaSwitchingMode.QualifiedNames,
            new StaticCurrentSchema(schema)));

        (exception is ArgumentException or InvalidOperationException).ShouldBeTrue();
        _commands.CommandTexts.Count.ShouldBe(commandCount);
    }

    [Theory]
    [InlineData(SchemaSwitchingMode.TransactionLocal)]
    [InlineData(SchemaSwitchingMode.SessionSearchPath)]
    public void Raw_SQL_token_is_rejected_outside_qualified_names_mode(SchemaSwitchingMode mode)
    {
        const string schema = "tenant";
        var interceptor = new SearchPathCommandInterceptor(
            schema,
            new SchemaScopeState(),
            mode,
            new StaticCurrentSchema(schema));
        using var command = new NpgsqlCommand("SELECT * FROM {{schema}}.\"things\"");

        var exception = Should.Throw<InvalidOperationException>(() =>
            interceptor.ReaderExecuting(command, null!, default));

        exception.Message.ShouldContain("QualifiedNames");
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
