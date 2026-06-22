using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BBT.Aether.Uow.EntityFrameworkCore;

/// <summary>
/// Prepends <c>SET LOCAL search_path</c> to every command issued by a schema-bound
/// <see cref="Microsoft.EntityFrameworkCore.DbContext"/>.
/// <para>
/// When multiple schema-bound contexts share a single connection and transaction (the
/// multi-schema UnitOfWork model), a one-time <c>SET LOCAL search_path</c> is insufficient:
/// <c>SET LOCAL</c> is transaction-scoped, so the most recently set search_path would apply to
/// every subsequent statement regardless of which context issued it. Issuing the search_path
/// as a prefix on each command guarantees correct schema resolution per command, which is also
/// required under PgBouncer transaction pooling.
/// </para>
/// <remarks>
/// Assumes query results are buffered (EF Core's default). Because a single Npgsql connection
/// does not support multiple active result sets, issuing a command while an un-buffered/streaming
/// <see cref="DbDataReader"/> from a sibling schema-bound context is still open on the same
/// connection will fail. Within one UnitOfWork, do not stream (e.g. <c>AsAsyncEnumerable</c>
/// without materializing) across interleaved schema-bound contexts.
/// </remarks>
/// </summary>
public sealed class SearchPathCommandInterceptor(string schema, SearchPathState state) : DbCommandInterceptor
{
    private readonly string _schema = schema;
    private readonly string _setSearchPath = $"SET LOCAL search_path TO {PostgreSqlIdentifier.QuoteSchema(schema)}, public";

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        ApplySearchPath(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await ApplySearchPathAsync(command, cancellationToken);
        return result;
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        ApplySearchPath(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await ApplySearchPathAsync(command, cancellationToken);
        return result;
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        ApplySearchPath(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        await ApplySearchPathAsync(command, cancellationToken);
        return result;
    }

    // Run SET LOCAL search_path as its own command (same connection + transaction) right before
    // the intercepted command, rather than concatenating it onto the command text. Concatenating
    // adds an extra result set that breaks EF's rows-affected accounting for INSERT/UPDATE batches.
    private void ApplySearchPath(DbCommand command)
    {
        if (command.Transaction is null)
        {
            throw new System.InvalidOperationException(
                "SearchPathCommandInterceptor requires the command to run inside the UnitOfWork transaction; Transaction was null. SET LOCAL search_path would be silently ignored, breaking schema isolation.");
        }

        // Skip the redundant SET when the shared connection already has this schema applied.
        if (state.Current == _schema)
        {
            return;
        }

        using var setCmd = command.Connection!.CreateCommand();
        setCmd.Transaction = command.Transaction;
        setCmd.CommandText = _setSearchPath;
        setCmd.ExecuteNonQuery();

        state.Current = _schema;
    }

    private async Task ApplySearchPathAsync(DbCommand command, CancellationToken cancellationToken)
    {
        if (command.Transaction is null)
        {
            throw new System.InvalidOperationException(
                "SearchPathCommandInterceptor requires the command to run inside the UnitOfWork transaction; Transaction was null. SET LOCAL search_path would be silently ignored, breaking schema isolation.");
        }

        // Skip the redundant SET when the shared connection already has this schema applied.
        if (state.Current == _schema)
        {
            return;
        }

        await using var setCmd = command.Connection!.CreateCommand();
        setCmd.Transaction = command.Transaction;
        setCmd.CommandText = _setSearchPath;
        await setCmd.ExecuteNonQueryAsync(cancellationToken);

        state.Current = _schema;
    }
}
