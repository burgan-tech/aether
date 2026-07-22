using System.Data.Common;
using BBT.Aether.MultiSchema;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BBT.Aether.Uow.EntityFrameworkCore;

/// <summary>
/// PostgreSQL provider for the multi-schema Unit of Work. Schema targeting always uses
/// <see cref="SchemaSwitchingMode.QualifiedNames"/>: SQL is rewritten to fully-qualified
/// <c>"schema"."table"</c> names, so no connection-level state is required and contexts can
/// run either on the UnitOfWork's shared transactional connection or on EF Core-owned pooled
/// connections.
/// </summary>
public sealed class NpgsqlAetherProvider : IAetherDatabaseProvider
{
    public DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

    public void ApplyShared(DbContextOptionsBuilder builder, DbConnection sharedConnection,
        string schema, SchemaScopeState state) =>
        ApplyShared(builder, sharedConnection, schema, state, new StaticCurrentSchema(schema));

    public void ApplyShared(DbContextOptionsBuilder builder, DbConnection sharedConnection,
        string schema, SchemaScopeState state, ICurrentSchema currentSchema)
    {
        builder.UseNpgsql(sharedConnection);
        ApplySchemaBinding(builder, schema, currentSchema);
    }

    public void ApplyOwned(DbContextOptionsBuilder builder, string connectionString,
        string schema, ICurrentSchema currentSchema)
    {
        builder.UseNpgsql(connectionString);
        ApplySchemaBinding(builder, schema, currentSchema);
    }

    public void ApplyConnectionString(DbContextOptionsBuilder builder, string connectionString)
        => builder.UseNpgsql(connectionString);

    private static void ApplySchemaBinding(DbContextOptionsBuilder builder, string schema,
        ICurrentSchema currentSchema)
    {
        builder.AddInterceptors(new QualifiedNamesCommandInterceptor(schema, currentSchema));
        builder.UseAetherQualifiedNamesModel();
    }
}
