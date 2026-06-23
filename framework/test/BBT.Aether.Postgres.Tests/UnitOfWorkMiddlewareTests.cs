using System;
using System.Threading.Tasks;
using BBT.Aether.AspNetCore.Middleware;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.MultiSchema;
using BBT.Aether.Uow;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;
using Thing = BBT.Aether.Postgres.Tests.MultiSchemaUnitOfWorkTests.Thing;
using ProbeDbContext = BBT.Aether.Postgres.Tests.MultiSchemaUnitOfWorkTests.ProbeDbContext;

namespace BBT.Aether.Postgres.Tests;

[Collection("postgres")]
public sealed class UnitOfWorkMiddlewareTests(PostgresFixture fx)
{
    private readonly string _schema = "mw_" + Guid.NewGuid().ToString("N");

    private async Task ArrangeSchemaAsync()
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"""
             CREATE SCHEMA {_schema};
             CREATE TABLE {_schema}.things ("Id" uuid PRIMARY KEY, "Name" text NOT NULL);
             """;
        await cmd.ExecuteNonQueryAsync();
    }

    private IUnitOfWorkManager BuildManager()
    {
        var services = new ServiceCollection();
        services.AddSingleton<BBT.Aether.Clock.IClock, BBT.Aether.Clock.SystemClock>();
        services.AddSingleton<ICurrentSchema>(new StaticCurrentSchema(_schema));
        services.AddSingleton<IAetherDbContextConfigurator<ProbeDbContext>>(
            new AetherDbContextConfigurator<ProbeDbContext>(
                fx.ConnectionString,
                new NpgsqlAetherProvider(),
                configure: (_, _) => { },
                serviceProvider: null!));
        var sp = services.BuildServiceProvider();
        return new UnitOfWorkManager(new AsyncLocalAmbientUowAccessor(), sp);
    }

    private async Task<long> CountAsync()
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {_schema}.things";
        var result = await cmd.ExecuteScalarAsync();
        return (long)result!;
    }

    private static UnitOfWorkMiddleware BuildMiddleware(IUnitOfWorkManager mgr) =>
        new(mgr, Options.Create(new UnitOfWorkMiddlewareOptions()));

    [Fact]
    public async Task Active_UnitOfWork_is_available_inside_next_for_a_non_excluded_path()
    {
        await ArrangeSchemaAsync();
        var mgr = BuildManager();
        var middleware = BuildMiddleware(mgr);

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/orders";

        var sawActiveUow = false;
        RequestDelegate next = _ =>
        {
            sawActiveUow = mgr.Current is not null;
            return Task.CompletedTask;
        };

        await middleware.InvokeAsync(ctx, next);

        sawActiveUow.ShouldBeTrue();
    }

    [Fact]
    public async Task Middleware_commits_on_success()
    {
        await ArrangeSchemaAsync();
        var mgr = BuildManager();
        var middleware = BuildMiddleware(mgr);

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/orders";

        RequestDelegate next = async _ =>
        {
            var uow = (IEfCoreUnitOfWork)mgr.Current!;
            var db = await uow.GetDbContextAsync<ProbeDbContext>(_schema);
            db.Set<Thing>().Add(new Thing { Id = Guid.NewGuid(), Name = "x" });
        };

        await middleware.InvokeAsync(ctx, next);

        (await CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Middleware_rolls_back_on_exception()
    {
        await ArrangeSchemaAsync();
        var mgr = BuildManager();
        var middleware = BuildMiddleware(mgr);

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/orders";

        RequestDelegate next = async _ =>
        {
            var uow = (IEfCoreUnitOfWork)mgr.Current!;
            var db = await uow.GetDbContextAsync<ProbeDbContext>(_schema);
            db.Set<Thing>().Add(new Thing { Id = Guid.NewGuid(), Name = "x" });
            throw new InvalidOperationException("boom");
        };

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await middleware.InvokeAsync(ctx, next));

        (await CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Excluded_path_runs_next_without_a_UnitOfWork()
    {
        await ArrangeSchemaAsync();
        var mgr = BuildManager();
        var middleware = BuildMiddleware(mgr);

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/health";

        var sawNoUow = false;
        RequestDelegate next = _ =>
        {
            sawNoUow = mgr.Current is null;
            return Task.CompletedTask;
        };

        await middleware.InvokeAsync(ctx, next);

        sawNoUow.ShouldBeTrue();
    }
}
