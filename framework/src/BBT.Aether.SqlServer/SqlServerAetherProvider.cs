using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Uow.EntityFrameworkCore;

/// <summary>
/// SQL Server provider for the Aether Unit of Work. SQL Server has no transaction-scoped
/// search_path equivalent, so this provider is SINGLE-SCHEMA: it supplies the shared
/// connection/transaction and binds options, but does NOT switch schema per command.
/// </summary>
/// <remarks>
/// On SQL Server the schema is fixed by the EF model (e.g. <c>modelBuilder.HasDefaultSchema("x")</c>
/// or schema-qualified <c>ToTable</c>). Calls to <c>ICurrentSchema.Change(...)</c> do NOT change the
/// schema used for queries on this provider — the requested schema argument is ignored by
/// <see cref="ApplyShared"/>. Do not rely on per-request/multi-schema switching on SQL Server; use
/// the PostgreSQL provider (search_path-based) when true multi-schema is required. Full SQL Server
/// multi-schema (per-schema compiled models) is a future enhancement.
/// </remarks>
public sealed class SqlServerAetherProvider : IAetherDatabaseProvider
{
    public bool RequiresTransaction => false;

    public DbConnection CreateConnection(string connectionString) => new SqlConnection(connectionString);

    public void ApplyShared(DbContextOptionsBuilder builder, DbConnection sharedConnection,
        string schema, SchemaScopeState state)
        => builder.UseSqlServer(sharedConnection);

    public void ApplyConnectionString(DbContextOptionsBuilder builder, string connectionString)
        => builder.UseSqlServer(connectionString);
}
