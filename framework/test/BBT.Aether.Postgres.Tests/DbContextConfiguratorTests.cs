using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Shouldly;
using Xunit;

namespace BBT.Aether.Postgres.Tests;

[Collection("postgres")]
public sealed class DbContextConfiguratorTests(PostgresFixture fx)
{
    [Fact]
    public async Task BuildOptions_binds_shared_connection_and_keeps_interceptors()
    {
        var interceptor = new CountingInterceptor();
        Action<IServiceProvider, DbContextOptionsBuilder> configure = (_, b) =>
        {
            b.UseNpgsql(fx.ConnectionString);            // connection-string based, like a real consumer
            b.AddInterceptors(interceptor);
        };

        var configurator = new AetherDbContextConfigurator<ProbeDbContext>(
            fx.ConnectionString, new NpgsqlAetherProvider(), configure, serviceProvider: new MinimalServiceProvider());

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();

        // The search_path interceptor added by the provider requires the command to run inside a
        // transaction, so start one on the shared connection and enlist the context on it.
        await using var tx = await conn.BeginTransactionAsync();

        var options = configurator.BuildOptions(conn, "public", new SchemaScopeState());
        await using var ctx = new ProbeDbContext(options);
        await ctx.Database.UseTransactionAsync(tx);

        // The context uses the SHARED connection we opened.
        ctx.Database.GetDbConnection().ShouldBeSameAs(conn);

        // A trivial command executes and the interceptor fired (a SET LOCAL search_path also runs).
        await ctx.Database.ExecuteSqlRawAsync("SELECT 1");
        interceptor.Commands.ShouldBeGreaterThan(0);

        await tx.CommitAsync();
    }

    private sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options) : DbContext(options);

    private sealed class CountingInterceptor : DbCommandInterceptor
    {
        public int Commands;

        public override System.Data.Common.DbCommand CommandCreated(
            CommandEndEventData eventData,
            System.Data.Common.DbCommand result)
        {
            Interlocked.Increment(ref Commands);
            return base.CommandCreated(eventData, result);
        }
    }

    private sealed class MinimalServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
