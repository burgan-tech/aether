using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.BackgroundJob;
using BBT.Aether.Clock;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BBT.Aether.Npgsql.BackgroundJob;

/// <summary>
/// PostgreSQL-specific implementation of <see cref="IWorkerSlotStore"/> that uses
/// <c>FOR UPDATE SKIP LOCKED</c> for contention-free slot acquisition.
/// </summary>
public class NpgsqlWorkerSlotStore<TDbContext>(
    IAetherDbContextProvider<TDbContext> dbContextProvider,
    IClock clock) : IWorkerSlotStore
    where TDbContext : DbContext, IHasEfCoreWorkerSlots
{
    /// <inheritdoc />
    public async Task<int?> TryAcquireSlotAsync(
        string workerName,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var entityType = dbContext.Model.FindEntityType(typeof(WorkerSlot))!;
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

        await using var command = connection.CreateCommand();
        command.Transaction = dbTransaction;

        // Acquire slot already owned by this pod (renew) OR claim any free/expired slot.
        // FOR UPDATE SKIP LOCKED prevents two pods from racing on the same free slot.
        command.CommandText = $"""
            UPDATE {fullTableName}
            SET "OwnerId"     = @ownerId,
                "LockedUntil" = @lockedUntil,
                "UpdatedAt"   = @now
            WHERE ("WorkerName", "SlotNo") = (
                SELECT "WorkerName", "SlotNo"
                FROM {fullTableName}
                WHERE "WorkerName" = @workerName
                  AND (
                      "OwnerId" = @ownerId
                      OR "LockedUntil" IS NULL
                      OR "LockedUntil" < @now
                  )
                ORDER BY
                    CASE WHEN "OwnerId" = @ownerId THEN 0 ELSE 1 END,
                    "SlotNo"
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            )
            RETURNING "SlotNo";
            """;

        AddParameter(command, "@workerName",  workerName);
        AddParameter(command, "@ownerId",     ownerId);
        AddParameter(command, "@lockedUntil", lockedUntil);
        AddParameter(command, "@now",         now);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result == null || result == DBNull.Value ? null : (int)result;
    }

    /// <inheritdoc />
    public async Task<bool> RenewSlotAsync(
        string workerName,
        int slotNo,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var entityType = dbContext.Model.FindEntityType(typeof(WorkerSlot))!;
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

        await using var command = connection.CreateCommand();
        command.Transaction = dbTransaction;
        command.CommandText = $"""
            UPDATE {fullTableName}
            SET "LockedUntil" = @lockedUntil,
                "UpdatedAt"   = @now
            WHERE "WorkerName" = @workerName
              AND "SlotNo"     = @slotNo
              AND "OwnerId"    = @ownerId;
            """;

        AddParameter(command, "@workerName",  workerName);
        AddParameter(command, "@slotNo",      slotNo);
        AddParameter(command, "@ownerId",     ownerId);
        AddParameter(command, "@lockedUntil", lockedUntil);
        AddParameter(command, "@now",         now);

        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    /// <inheritdoc />
    public async Task ReleaseSlotAsync(
        string workerName,
        int slotNo,
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var entityType = dbContext.Model.FindEntityType(typeof(WorkerSlot))!;
        var tableName = entityType.GetTableName();
        var schema = entityType.GetSchema();
        var fullTableName = string.IsNullOrEmpty(schema)
            ? $"\"{tableName}\""
            : $"\"{schema}\".\"{tableName}\"";

        var connection = dbContext.Database.GetDbConnection();
        var now = clock.UtcNow;

        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        var dbTransaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();

        await using var command = connection.CreateCommand();
        command.Transaction = dbTransaction;
        command.CommandText = $"""
            UPDATE {fullTableName}
            SET "OwnerId"     = NULL,
                "LockedUntil" = NULL,
                "UpdatedAt"   = @now
            WHERE "WorkerName" = @workerName
              AND "SlotNo"     = @slotNo
              AND "OwnerId"    = @ownerId;
            """;

        AddParameter(command, "@workerName", workerName);
        AddParameter(command, "@slotNo",     slotNo);
        AddParameter(command, "@ownerId",    ownerId);
        AddParameter(command, "@now",        now);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }
}
