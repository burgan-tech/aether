using System;
using System.Data.Common;
using System.Text;
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
///     <description>
///       Rewrites the model's exact schema placeholder to the context-bound qualified schema.
///       Throws if the current schema no longer matches that binding.
///     </description>
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
    SchemaSwitchingMode mode,
    ICurrentSchema currentSchema) : DbCommandInterceptor
{
    // EF Core applies composite formatting before command interception, so the public
    // {{schema}} token arrives as {schema} whenever the raw SQL call has parameters.
    private const string FormattedRawSqlToken = "{schema}";
    private readonly string _schema = schema;
    private readonly string _quotedSchema = PostgreSqlIdentifier.QuoteSchema(schema);
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
        RejectRawSqlTokenOutsideQualifiedNames(command.CommandText);

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
                ApplyQualifiedNames(command);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown SchemaSwitchingMode.");
        }
    }

    private async Task ApplySearchPathAsync(DbCommand command, CancellationToken cancellationToken)
    {
        RejectRawSqlTokenOutsideQualifiedNames(command.CommandText);

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
                ApplyQualifiedNames(command);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown SchemaSwitchingMode.");
        }
    }

    private void ApplyQualifiedNames(DbCommand command)
    {
        if (!string.Equals(currentSchema.Name, _schema, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"DbContext is bound to schema '{_schema}', but current schema is " +
                $"'{currentSchema.Name ?? "<none>"}'. Resolve the DbContext again inside the new schema scope.");

        command.CommandText = RewriteModelPlaceholder(command.CommandText)
            .Replace(AetherSchemaModel.RawSqlToken, _quotedSchema, StringComparison.Ordinal)
            .Replace(FormattedRawSqlToken, _quotedSchema, StringComparison.Ordinal);
    }

    private string RewriteModelPlaceholder(string commandText)
    {
        var rewritten = new StringBuilder(commandText.Length);
        var position = 0;

        while (position < commandText.Length)
        {
            var quotedIndex = commandText.IndexOf(
                AetherSchemaModel.QuotedPlaceholder,
                position,
                StringComparison.Ordinal);
            var unquotedIndex = commandText.IndexOf(
                AetherSchemaModel.Placeholder,
                position,
                StringComparison.Ordinal);

            if (quotedIndex < 0 && unquotedIndex < 0)
            {
                rewritten.Append(commandText, position, commandText.Length - position);
                break;
            }

            var replaceQuoted = quotedIndex >= 0 &&
                                (unquotedIndex < 0 || quotedIndex <= unquotedIndex);
            var placeholderIndex = replaceQuoted ? quotedIndex : unquotedIndex;
            var placeholderLength = replaceQuoted
                ? AetherSchemaModel.QuotedPlaceholder.Length
                : AetherSchemaModel.Placeholder.Length;

            rewritten.Append(commandText, position, placeholderIndex - position);
            rewritten.Append(_quotedSchema);
            position = placeholderIndex + placeholderLength;
        }

        return rewritten.ToString();
    }

    private void RejectRawSqlTokenOutsideQualifiedNames(string commandText)
    {
        if (mode != SchemaSwitchingMode.QualifiedNames &&
            (commandText.Contains(AetherSchemaModel.RawSqlToken, StringComparison.Ordinal) ||
             commandText.Contains(FormattedRawSqlToken, StringComparison.Ordinal)))
            throw new InvalidOperationException(
                $"Raw SQL token '{AetherSchemaModel.RawSqlToken}' requires " +
                $"SchemaSwitchingMode.QualifiedNames. In {mode} mode, omit the token and rely on " +
                "the documented search_path contract.");
    }
}
