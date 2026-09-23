using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.MultiSchema;
using BBT.Aether.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BBT.Aether.Events;

/// <summary>
/// PostgreSQL-specific implementation of <see cref="IInboxCleanupStore"/> that uses
/// <c>FOR UPDATE SKIP LOCKED</c> so concurrent workers delete disjoint batches: a row another
/// worker is deleting is skipped rather than waited on, and a row already deleted is simply not
/// counted — neither surfaces as a concurrency failure.
/// </summary>
public class NpgsqlInboxCleanupStore<TDbContext>(
    IAetherDbContextProvider<TDbContext> dbContextProvider,
    ICurrentSchema currentSchema,
    IClock clock) : IInboxCleanupStore
    where TDbContext : DbContext, IHasEfCoreInbox
{
    /// <inheritdoc />
    public async Task<int> DeleteProcessedAsync(int batchSize, TimeSpan retentionPeriod,
        CancellationToken cancellationToken = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var entityType = dbContext.Model.FindEntityType(typeof(BBT.Aether.Domain.Events.InboxMessage))!;
        var schema = currentSchema.Name
            ?? throw new InvalidOperationException("Current schema is not set.");
        var fullTableName = PostgreSqlRelationName.For(entityType, schema);

        var connection = dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = $"""
            DELETE FROM {fullTableName}
            WHERE "Id" IN (
                SELECT "Id"
                FROM {fullTableName}
                WHERE "Status" = @processed
                  AND "HandledTime" IS NOT NULL
                  AND "HandledTime" < @cutoff
                ORDER BY "HandledTime"
                LIMIT @batchSize
                FOR UPDATE SKIP LOCKED
            );
            """;

        AddParameter(command, "@processed", (int)IncomingEventStatus.Processed);
        AddParameter(command, "@cutoff",    clock.UtcNow - retentionPeriod);
        AddParameter(command, "@batchSize", batchSize);

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }
}
