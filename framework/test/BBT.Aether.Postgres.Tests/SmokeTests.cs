using System.Threading.Tasks;
using Npgsql;
using Shouldly;
using Xunit;

namespace BBT.Aether.Postgres.Tests;

[Collection("postgres")]
public sealed class SmokeTests(PostgresFixture fx)
{
    [Fact]
    public async Task Container_accepts_connections()
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT 1", conn);
        (await cmd.ExecuteScalarAsync()).ShouldBe(1);
    }
}
