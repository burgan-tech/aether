using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Uow.EntityFrameworkCore;

/// <summary>
/// SQL Server provider for the Aether Unit of Work. SQL Server has no transaction-scoped
/// search_path equivalent, so this provider is SINGLE-SCHEMA: it supplies the shared
/// connection/transaction and binds options, but does NOT switch schema per command.
/// Bind your schema in the model (e.g. modelBuilder.HasDefaultSchema("x") or schema-qualified
/// ToTable). Full SQL Server multi-schema (per-schema compiled model) is a future enhancement.
/// </summary>
public sealed class SqlServerAetherProvider : IAetherDatabaseProvider
{
    public DbConnection CreateConnection(string connectionString) => new SqlConnection(connectionString);

    public void ApplyShared(DbContextOptionsBuilder builder, DbConnection sharedConnection,
        string schema, SchemaScopeState state)
        => builder.UseSqlServer(sharedConnection);

    public void ApplyConnectionString(DbContextOptionsBuilder builder, string connectionString)
        => builder.UseSqlServer(connectionString);
}
