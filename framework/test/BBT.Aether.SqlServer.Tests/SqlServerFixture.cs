using System;
using System.Threading.Tasks;
using Testcontainers.MsSql;
using Xunit;

namespace BBT.Aether.SqlServer.Tests;

/// <summary>
/// Shared SQL Server container fixture (Testcontainers MsSql).
/// <para>
/// The <c>mcr.microsoft.com/mssql/server</c> image is large (~1.5GB) and the container needs
/// time to become healthy. Set the environment variable <c>AETHER_SKIP_MSSQL=1</c> to opt out
/// of starting the container; tests then return early (soft skip) so CI without the image stays
/// green.
/// </para>
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    /// <summary>Skip the SQL Server suite when <c>AETHER_SKIP_MSSQL=1</c> is set.</summary>
    public static bool Skip => Environment.GetEnvironmentVariable("AETHER_SKIP_MSSQL") == "1";

    public MsSqlContainer Container { get; } = new MsSqlBuilder().Build();
    public string ConnectionString => Container.GetConnectionString();

    public async Task InitializeAsync()
    {
        if (Skip) return;
        await Container.StartAsync();
    }

    public Task DisposeAsync() => Skip ? Task.CompletedTask : Container.DisposeAsync().AsTask();
}

[CollectionDefinition("sqlserver")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture> { }
