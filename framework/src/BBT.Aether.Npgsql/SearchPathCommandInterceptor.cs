using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BBT.Aether.Uow.EntityFrameworkCore;

/// <summary>
/// Sets the active PostgreSQL <c>search_path</c> before each command issued by a schema-bound
/// <see cref="Microsoft.EntityFrameworkCore.DbContext"/>. Behaviour depends on
/// <see cref="SchemaSwitchingMode"/>:
/// <list type="bullet">
///   <item>
///     <term><see cref="SchemaSwitchingMode.TransactionLocal"/></term>
///     <description>
///       Issues <c>SET LOCAL search_path</c> inside the active transaction.
///       PostgreSQL reverts the effect at transaction end automatically.
///       Throws if the command has no transaction.
///     </description>
///   </item>
///   <item>
///     <term><see cref="SchemaSwitchingMode.SessionSearchPath"/></term>
///     <description>
///       Issues a session-level <c>SET search_path</c> when the schema changes.
///       The caller (UnitOfWork dispose) is responsible for running <c>RESET search_path</c>
///       before returning the connection to the pool via <see cref="SchemaScopeState.Cleanup"/>.
///     </description>
///   </item>
///   <item>
///     <term><see cref="SchemaSwitchingMode.QualifiedNames"/></term>
///     <description>Not yet implemented — throws <see cref="NotSupportedException"/>.</description>
///   </item>
/// </list>
/// <remarks>
/// Assumes query results are buffered (EF Core's default). A single Npgsql connection does not
/// support multiple active result sets; do not stream (<c>AsAsyncEnumerable</c> without
/// materializing) across interleaved schema-bound contexts on the same connection.
/// </remarks>
/// </summary>
public sealed class SearchPathCommandInterceptor(
    string schema,
    SchemaScopeState state,
    SchemaSwitchingMode mode) : DbCommandInterceptor
{
    private readonly string _schema = schema;
    private readonly string _setLocal =
        $"SET LOCAL search_path TO {PostgreSqlIdentifier.QuoteSchema(schema)}, public";
    private readonly string _setSession =
        $"SET search_path TO {PostgreSqlIdentifier.QuoteSchema(schema)}, public";

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

    private void ApplySearchPath(DbCommand command)
    {
        switch (mode)
        {
            case SchemaSwitchingMode.TransactionLocal:
                if (command.Transaction is null)
                {
                    throw new InvalidOperationException(
                        $"SchemaSwitchingMode.TransactionLocal requires a transaction, but none is active. " +
                        $"Use IsTransactional = true, or switch to SchemaSwitchingMode.SessionSearchPath " +
                        $"(direct/session pool) or SchemaSwitchingMode.QualifiedNames (PgBouncer transaction pool).");
                }
                if (state.Current == _schema) return;
                using (var cmd = command.Connection!.CreateCommand())
                {
                    cmd.Transaction = command.Transaction;
                    cmd.CommandText = _setLocal;
                    cmd.ExecuteNonQuery();
                }
                state.Current = _schema;
                break;

            case SchemaSwitchingMode.SessionSearchPath:
                if (state.Current == _schema) return;
                using (var cmd = command.Connection!.CreateCommand())
                {
                    cmd.CommandText = _setSession;
                    cmd.ExecuteNonQuery();
                }
                state.Current = _schema;
                break;

            case SchemaSwitchingMode.QualifiedNames:
                throw new NotSupportedException(
                    "SchemaSwitchingMode.QualifiedNames is not yet implemented.");

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown SchemaSwitchingMode.");
        }
    }

    private async Task ApplySearchPathAsync(DbCommand command, CancellationToken cancellationToken)
    {
        switch (mode)
        {
            case SchemaSwitchingMode.TransactionLocal:
                if (command.Transaction is null)
                {
                    throw new InvalidOperationException(
                        $"SchemaSwitchingMode.TransactionLocal requires a transaction, but none is active. " +
                        $"Use IsTransactional = true, or switch to SchemaSwitchingMode.SessionSearchPath " +
                        $"(direct/session pool) or SchemaSwitchingMode.QualifiedNames (PgBouncer transaction pool).");
                }
                if (state.Current == _schema) return;
                await using (var cmd = command.Connection!.CreateCommand())
                {
                    cmd.Transaction = command.Transaction;
                    cmd.CommandText = _setLocal;
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }
                state.Current = _schema;
                break;

            case SchemaSwitchingMode.SessionSearchPath:
                if (state.Current == _schema) return;
                await using (var cmd = command.Connection!.CreateCommand())
                {
                    cmd.CommandText = _setSession;
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }
                state.Current = _schema;
                break;

            case SchemaSwitchingMode.QualifiedNames:
                throw new NotSupportedException(
                    "SchemaSwitchingMode.QualifiedNames is not yet implemented.");

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown SchemaSwitchingMode.");
        }
    }
}
