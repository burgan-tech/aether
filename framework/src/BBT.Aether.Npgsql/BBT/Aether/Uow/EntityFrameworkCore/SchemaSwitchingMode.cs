namespace BBT.Aether.Uow.EntityFrameworkCore;

/// <summary>
/// Controls how a schema-bound Npgsql DbContext targets the active PostgreSQL schema.
/// <see cref="QualifiedNames"/> is the only supported strategy: the former
/// <c>TransactionLocal</c> and <c>SessionSearchPath</c> modes manipulated the connection's
/// <c>search_path</c> and therefore required the UnitOfWork to pin a single shared connection.
/// Qualified names keep every command self-describing, so connections can be pooled and
/// owned by EF Core (including PgBouncer transaction pooling).
/// </summary>
public enum SchemaSwitchingMode
{
    /// <summary>
    /// Rewrites SQL to use fully-qualified <c>"schema"."table"</c> names. No <c>search_path</c>
    /// manipulation is performed, so commands are safe on any pooled connection.
    /// <para>
    /// Schema-dependent raw SQL must use the explicit <c>{{schema}}</c> token; arbitrary SQL is
    /// not parsed or automatically qualified.
    /// </para>
    /// </summary>
    QualifiedNames,
}
