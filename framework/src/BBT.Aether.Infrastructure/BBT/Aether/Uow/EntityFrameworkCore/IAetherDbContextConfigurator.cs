using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Uow.EntityFrameworkCore;

/// <summary>
/// Builds <see cref="DbContextOptions{TDbContext}"/> bound to a caller-supplied open
/// connection, applying the same provider configuration and interceptors registered
/// via AddAetherDbContext. Used by the UnitOfWork to create schema-bound contexts that
/// all enlist on one shared connection/transaction.
/// </summary>
public interface IAetherDbContextConfigurator<TDbContext>
    where TDbContext : DbContext
{
    /// <summary>Creates a new provider-specific connection for the UnitOfWork's single shared connection.</summary>
    DbConnection CreateConnection();

    /// <summary>Builds options that use the given already-open shared connection, bound to the given schema.</summary>
    DbContextOptions<TDbContext> BuildOptions(DbConnection sharedConnection, string schema, SchemaScopeState state);
}
