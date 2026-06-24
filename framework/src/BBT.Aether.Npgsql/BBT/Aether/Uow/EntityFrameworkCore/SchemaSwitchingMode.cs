namespace BBT.Aether.Uow.EntityFrameworkCore;

/// <summary>
/// Controls how a schema-bound Npgsql DbContext switches the active PostgreSQL search_path.
/// Choose based on your connection pool topology.
/// </summary>
public enum SchemaSwitchingMode
{
    /// <summary>
    /// Issues <c>SET LOCAL search_path</c> before each command.
    /// The effect is automatically reverted at transaction end by PostgreSQL.
    /// <para>Requires <c>IsTransactional = true</c>. Works with any connection pool.</para>
    /// </summary>
    TransactionLocal,

    /// <summary>
    /// Issues a session-level <c>SET search_path</c> before the first command to a given schema,
    /// then <c>RESET search_path</c> when the UnitOfWork is disposed (before the connection
    /// is returned to the pool).
    /// <para>
    /// Use with <c>IsTransactional = false</c> and Npgsql's native connection pool (direct or
    /// session pooling). NOT safe with PgBouncer transaction pooling because the session-level
    /// <c>SET</c> may not survive across PgBouncer backend switches.
    /// </para>
    /// </summary>
    SessionSearchPath,

    /// <summary>
    /// Rewrites SQL to use fully-qualified <c>"schema"."table"</c> names. No <c>search_path</c>
    /// manipulation is performed.
    /// <para>
    /// Intended for <c>IsTransactional = false</c> behind PgBouncer transaction pooling.
    /// </para>
    /// <para><b>Not yet implemented.</b> Throws <see cref="System.NotSupportedException"/>.</para>
    /// </summary>
    QualifiedNames,
}
