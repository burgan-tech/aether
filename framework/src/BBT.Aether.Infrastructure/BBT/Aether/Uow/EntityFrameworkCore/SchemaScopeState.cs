using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Uow.EntityFrameworkCore;

/// <summary>
/// Tracks the schema most recently applied on a UnitOfWork's single shared connection,
/// letting a provider's schema interceptor skip a redundant re-apply when consecutive
/// commands target the same schema. Not thread-safe by design: commands on a single
/// connection are serialized and one instance is scoped to one UnitOfWork.
/// </summary>
public sealed class SchemaScopeState
{
    /// <summary>The schema name most recently written to the connection's search context.</summary>
    public string? Current { get; set; }

    /// <summary>
    /// Optional cleanup to run just before the shared connection is disposed (i.e. returned to
    /// the pool). Set by providers that use session-level state (e.g. <c>SET search_path</c>) so
    /// that the state is always reset before the next caller borrows the connection.
    /// <para>Only invoked when <see cref="Current"/> is non-null (meaning the state was actually
    /// written at least once during this UnitOfWork).</para>
    /// </summary>
    public Func<DbConnection, CancellationToken, Task>? Cleanup { get; set; }
}
