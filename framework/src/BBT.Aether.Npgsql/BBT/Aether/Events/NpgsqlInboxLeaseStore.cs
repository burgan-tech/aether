using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
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
/// PostgreSQL-specific implementation of <see cref="IInboxLeaseStore"/> that uses
/// <c>FOR UPDATE SKIP LOCKED</c> for efficient, contention-free batch leasing.
/// </summary>
public class NpgsqlInboxLeaseStore<TDbContext>(
    IAetherDbContextProvider<TDbContext> dbContextProvider,
    ICurrentSchema currentSchema,
    IClock clock) : IInboxLeaseStore
    where TDbContext : DbContext, IHasEfCoreInbox
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<InboxMessage>> LeaseBatchAsync(
        int batchSize,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var entityType = dbContext.Model.FindEntityType(typeof(BBT.Aether.Domain.Events.InboxMessage))!;
        var tableName = entityType.GetTableName();
        var schema = entityType.GetSchema();
        var fullTableName = string.IsNullOrEmpty(schema)
            ? $"\"{tableName}\""
            : $"\"{schema}\".\"{tableName}\"";

        var connection = dbContext.Database.GetDbConnection();
        var now = clock.UtcNow;
        var lockedUntil = now.Add(leaseDuration);

        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        var dbTransaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();

        // When the entity has no baked-in schema (search_path mode), issue SET LOCAL search_path
        // so that the raw ADO.NET command lands in the correct schema. EF's command interceptor
        // normally does this for EF commands; we replicate it here for the raw UPDATE … RETURNING.
        if (string.IsNullOrEmpty(schema) && !string.IsNullOrEmpty(currentSchema.Name))
        {
            await using var setCmd = connection.CreateCommand();
            setCmd.Transaction = dbTransaction;
            setCmd.CommandText = $"SET LOCAL search_path TO \"{currentSchema.Name}\";";
            await setCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = dbTransaction;
        command.CommandText = $"""
            UPDATE {fullTableName}
            SET
                "Status"      = @processing,
                "LockedBy"    = @workerId,
                "LockedUntil" = @lockedUntil
            WHERE "Id" IN (
                SELECT "Id"
                FROM {fullTableName}
                WHERE "Status" = @pending
                  AND ("LockedUntil" IS NULL OR "LockedUntil" < @now)
                  AND ("NextRetryTime" IS NULL OR "NextRetryTime" <= @now)
                ORDER BY "CreatedAt"
                LIMIT @batchSize
                FOR UPDATE SKIP LOCKED
            )
            RETURNING "Id", "Status", "EventName", "EventData", "CreatedAt",
                      "HandledTime", "LockedBy", "LockedUntil", "RetryCount",
                      "NextRetryTime", "ExtraProperties";
            """;

        AddParameter(command, "@processing", (int)IncomingEventStatus.Processing);
        AddParameter(command, "@pending",    (int)IncomingEventStatus.Pending);
        AddParameter(command, "@workerId",   workerId);
        AddParameter(command, "@lockedUntil", lockedUntil);
        AddParameter(command, "@now",        now);
        AddParameter(command, "@batchSize",  batchSize);

        var messages = new List<InboxMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new InboxMessage
            {
                Id            = reader.GetString(reader.GetOrdinal("Id")),
                Status        = (IncomingEventStatus)reader.GetInt32(reader.GetOrdinal("Status")),
                EventName     = reader.GetString(reader.GetOrdinal("EventName")),
                EventData     = (byte[])reader["EventData"],
                CreatedAt     = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
                HandledTime   = reader.IsDBNull(reader.GetOrdinal("HandledTime"))   ? null : reader.GetDateTime(reader.GetOrdinal("HandledTime")),
                LockedBy      = reader.IsDBNull(reader.GetOrdinal("LockedBy"))      ? null : reader.GetString(reader.GetOrdinal("LockedBy")),
                LockedUntil   = reader.IsDBNull(reader.GetOrdinal("LockedUntil"))   ? null : reader.GetDateTime(reader.GetOrdinal("LockedUntil")),
                RetryCount    = reader.GetInt32(reader.GetOrdinal("RetryCount")),
                NextRetryTime = reader.IsDBNull(reader.GetOrdinal("NextRetryTime")) ? null : reader.GetDateTime(reader.GetOrdinal("NextRetryTime")),
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
