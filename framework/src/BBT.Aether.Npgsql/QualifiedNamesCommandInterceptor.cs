using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BBT.Aether.Uow.EntityFrameworkCore;

/// <summary>
/// Rewrites every command issued by a schema-bound
/// <see cref="Microsoft.EntityFrameworkCore.DbContext"/> to use fully-qualified
/// <c>"schema"."table"</c> names: the model's exact schema placeholder and the raw SQL
/// <c>{{schema}}</c> token are both replaced with the context-bound quoted schema.
/// No <c>search_path</c> manipulation is performed, so commands are safe on any pooled
/// connection (including PgBouncer transaction pooling) and on EF Core-owned connections.
/// Throws if the current schema no longer matches the context's schema binding.
/// <remarks>
/// Assumes query results are buffered (EF Core's default). When multiple schema-bound contexts
/// share one UnitOfWork connection, do not stream (<c>AsAsyncEnumerable</c> without
/// materializing) across interleaved contexts on the same connection.
/// </remarks>
/// </summary>
public sealed class QualifiedNamesCommandInterceptor(
    string schema,
    ICurrentSchema currentSchema) : DbCommandInterceptor
{
    private readonly string _schema = schema;
    private readonly string _quotedSchema = PostgreSqlIdentifier.QuoteSchema(schema);

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        ApplyQualifiedNames(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        ApplyQualifiedNames(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        ApplyQualifiedNames(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ApplyQualifiedNames(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        ApplyQualifiedNames(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        ApplyQualifiedNames(command);
        return ValueTask.FromResult(result);
    }

    private void ApplyQualifiedNames(DbCommand command)
    {
        if (!string.Equals(currentSchema.Name, _schema, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"DbContext is bound to schema '{_schema}', but current schema is " +
                $"'{currentSchema.Name ?? "<none>"}'. Resolve the DbContext again inside the new schema scope.");

        var modelRewritten = PostgreSqlRawSchemaTokenRewriter
            .RewriteModelPlaceholder(command.CommandText, _quotedSchema)
            .CommandText;
        command.CommandText = PostgreSqlRawSchemaTokenRewriter
            .Rewrite(modelRewritten, _quotedSchema)
            .CommandText;
    }
}
