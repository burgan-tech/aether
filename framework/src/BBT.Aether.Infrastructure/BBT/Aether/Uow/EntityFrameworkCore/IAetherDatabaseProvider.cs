using System.Data.Common;
using BBT.Aether.MultiSchema;
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

    /// <summary>
    /// Applies a shared connection with access to the runtime schema accessor. The default keeps
    /// providers compiled against the original four-argument contract source-compatible; providers
    /// that need runtime schema checks can override this overload.
    /// </summary>
    void ApplyShared(DbContextOptionsBuilder builder, DbConnection sharedConnection,
        string schema, SchemaScopeState state, ICurrentSchema currentSchema) =>
        ApplyShared(builder, sharedConnection, schema, state);

    /// <summary>
    /// Binds options to the connection string so EF Core creates, opens, and closes its own
    /// pooled connection per operation — no caller-managed shared connection is involved.
    /// Used by non-transactional units of work, where holding a physical connection for the
    /// UnitOfWork's lifetime is unnecessary pool pressure. The default keeps existing providers
    /// source-compatible; multi-schema providers should override to add their schema binding.
    /// </summary>
    void ApplyOwned(DbContextOptionsBuilder builder, string connectionString,
        string schema, ICurrentSchema currentSchema) =>
        ApplyConnectionString(builder, connectionString);

    void ApplyConnectionString(DbContextOptionsBuilder builder, string connectionString);
}
