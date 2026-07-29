using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text.Json;
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
/// PostgreSQL-specific implementation of <see cref="IOutboxLeaseStore"/> that uses
/// <c>FOR UPDATE SKIP LOCKED</c> for efficient, contention-free batch leasing.
/// </summary>
public class NpgsqlOutboxLeaseStore<TDbContext>(
    IAetherDbContextProvider<TDbContext> dbContextProvider,
    ICurrentSchema currentSchema,
    IClock clock) : IOutboxLeaseStore
    where TDbContext : DbContext, IHasEfCoreOutbox
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<OutboxMessage>> LeaseBatchAsync(
        int batchSize,
        string workerId,
        TimeSpan leaseDuration,
        IReadOnlyCollection<short>? partitionIds = null,
        CancellationToken cancellationToken = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var entityType = dbContext.Model.FindEntityType(typeof(BBT.Aether.Domain.Events.OutboxMessage))!;
        var schema = currentSchema.Name
            ?? throw new InvalidOperationException("Current schema is not set.");
        var fullTableName = PostgreSqlRelationName.For(entityType, schema);

        var connection = dbContext.Database.GetDbConnection();
        var now = clock.UtcNow;
        var lockedUntil = now.Add(leaseDuration);

        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        var dbTransaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();

        // Built conditionally rather than a single "(@p IS NULL OR ...)" predicate: the planner
        // only proves the partial dispatch index covers this query when it sees the status
        // values as constants, and a nullable-parameter predicate muddies that. An empty
        // collection is treated as unfiltered, not "match nothing" — ANY of an empty array
        // returns zero rows, which would silently stall dispatch instead of falling back to a
        // full sweep.
        var hasPartitionFilter = partitionIds is { Count: > 0 };
        var partitionFilter = hasPartitionFilter
            ? "\n                  AND \"PartitionId\" = ANY(@partitionIds)"
            : string.Empty;

        await using var command = connection.CreateCommand();
        command.Transaction = dbTransaction;
        command.CommandText = $"""
            UPDATE {fullTableName}
            SET
                "RetryCount"  = CASE WHEN "Status" = @processing
                                     THEN "RetryCount" + 1
                                     ELSE "RetryCount" END,
                "Status"      = @processing,
                "LockedBy"    = @workerId,
                "LockedUntil" = @lockedUntil
            WHERE "Id" IN (
                SELECT "Id"
                FROM {fullTableName}
                WHERE "Status" IN (@pending, @processing)
                  AND ("LockedUntil" IS NULL OR "LockedUntil" < @now)
                  AND ("NextRetryAt" IS NULL OR "NextRetryAt" <= @now){partitionFilter}
                ORDER BY "CreatedAt"
                LIMIT @batchSize
                FOR UPDATE SKIP LOCKED
            )
            RETURNING "Id", "Status", "EventName", "EventData", "CreatedAt",
                      "ProcessedAt", "LockedBy", "LockedUntil", "LastError",
                      "RetryCount", "NextRetryAt", "PartitionId", "ExtraProperties";
            """;

        AddParameter(command, "@processing", (int)OutboxMessageStatus.Processing);
        AddParameter(command, "@pending",    (int)OutboxMessageStatus.Pending);
        AddParameter(command, "@workerId",   workerId);
        AddParameter(command, "@lockedUntil", lockedUntil);
        AddParameter(command, "@now",        now);
        AddParameter(command, "@batchSize",  batchSize);

        if (hasPartitionFilter)
        {
            AddParameter(command, "@partitionIds", partitionIds!.ToArray());
        }

        var messages = new List<OutboxMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new OutboxMessage
            {
                Id          = reader.GetGuid(reader.GetOrdinal("Id")),
                Status      = (OutboxMessageStatus)reader.GetInt32(reader.GetOrdinal("Status")),
                EventName   = reader.GetString(reader.GetOrdinal("EventName")),
                EventData   = (byte[])reader["EventData"],
                CreatedAt   = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
                ProcessedAt = reader.IsDBNull(reader.GetOrdinal("ProcessedAt")) ? null : reader.GetDateTime(reader.GetOrdinal("ProcessedAt")),
                LockedBy    = reader.IsDBNull(reader.GetOrdinal("LockedBy"))    ? null : reader.GetString(reader.GetOrdinal("LockedBy")),
                LockedUntil = reader.IsDBNull(reader.GetOrdinal("LockedUntil")) ? null : reader.GetDateTime(reader.GetOrdinal("LockedUntil")),
                LastError   = reader.IsDBNull(reader.GetOrdinal("LastError"))   ? null : reader.GetString(reader.GetOrdinal("LastError")),
                RetryCount  = reader.GetInt32(reader.GetOrdinal("RetryCount")),
                NextRetryAt = reader.IsDBNull(reader.GetOrdinal("NextRetryAt")) ? null : reader.GetDateTime(reader.GetOrdinal("NextRetryAt")),
                PartitionId = reader.GetInt16(reader.GetOrdinal("PartitionId")),
                ExtraProperties = DeserializeExtraProperties(reader),
            });
        }

        return messages;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }

    private static Dictionary<string, object> DeserializeExtraProperties(DbDataReader reader)
    {
        var ordinal = reader.GetOrdinal("ExtraProperties");
        if (reader.IsDBNull(ordinal)) return new Dictionary<string, object>();
        var json = reader.GetString(ordinal);
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return new Dictionary<string, object>();
        return JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
    }
}
