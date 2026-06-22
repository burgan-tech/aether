using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Uow.EntityFrameworkCore;

/// <summary>
/// Encapsulates the only behaviour that differs by database engine for the multi-schema
/// Unit of Work: connection creation, binding options to a shared connection, and the
/// per-schema strategy. Selected at AddAetherDbContext time, resolved at runtime.
/// </summary>
public interface IAetherDatabaseProvider
{
    DbConnection CreateConnection(string connectionString);
    void ApplyShared(DbContextOptionsBuilder builder, DbConnection sharedConnection,
        string schema, SchemaScopeState state);
    void ApplyConnectionString(DbContextOptionsBuilder builder, string connectionString);
}
