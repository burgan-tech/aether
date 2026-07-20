using System.Data.Common;
using BBT.Aether.MultiSchema;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Aether.Infrastructure.Tests.BBT.Aether.Uow;

public sealed class DatabaseProviderCompatibilityTests
{
    [Fact]
    public void Existing_provider_implementing_original_ApplyShared_contract_remains_compatible()
    {
        IAetherDatabaseProvider provider = new LegacyProvider();
        var builder = new DbContextOptionsBuilder();
        var connection = Substitute.For<DbConnection>();

        provider.ApplyShared(builder, connection, "tenant", new SchemaScopeState(),
            new StaticCurrentSchema("tenant"));

        ((LegacyProvider)provider).WasApplied.ShouldBeTrue();
    }

    private sealed class LegacyProvider : IAetherDatabaseProvider
    {
        public bool WasApplied { get; private set; }
        public DbConnection CreateConnection(string connectionString) => Substitute.For<DbConnection>();
        public void ApplyShared(DbContextOptionsBuilder builder, DbConnection sharedConnection,
            string schema, SchemaScopeState state) => WasApplied = true;
        public void ApplyConnectionString(DbContextOptionsBuilder builder, string connectionString) { }
    }
}
