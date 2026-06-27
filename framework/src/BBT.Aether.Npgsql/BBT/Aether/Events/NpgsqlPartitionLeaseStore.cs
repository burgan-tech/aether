using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Events;
using BBT.Aether.Partitioning;
using BBT.Aether.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BBT.Aether.Npgsql.Events;

/// <summary>
/// PostgreSQL-specific implementation of <see cref="IPartitionLeaseStore"/> that atomically
/// acquires or renews partition leases using individual UPDATE statements guarded by owner/expiry checks.
/// </summary>
public class NpgsqlPartitionLeaseStore<TDbContext>(
    IAetherDbContextProvider<TDbContext> dbContextProvider,
    IClock clock) : IPartitionLeaseStore
    where TDbContext : DbContext, IHasEfCorePartitionLeases
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> AcquireOrRenewAsync(
        string workerName,
        string ownerId,
        int maxPartitions,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var entityType = dbContext.Model.FindEntityType(typeof(PartitionLease))!;
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

        // Step 1: find partitions already owned by this pod (renew them) and free/expired ones
        // we can claim, up to maxPartitions total.
        await using var selectCmd = connection.CreateCommand();
        selectCmd.Transaction = dbTransaction;
        selectCmd.CommandText = $"""
            SELECT "PartitionNo"
            FROM {fullTableName}
            WHERE "WorkerName" = @workerName
              AND (
                  "OwnerId" = @ownerId
                  OR "LockedUntil" IS NULL
                  OR "LockedUntil" < @now
              )
            ORDER BY
                CASE WHEN "OwnerId" = @ownerId THEN 0 ELSE 1 END,
                "PartitionNo"
            LIMIT @maxPartitions;
            """;

        AddParameter(selectCmd, "@workerName",   workerName);
        AddParameter(selectCmd, "@ownerId",      ownerId);
        AddParameter(selectCmd, "@now",          now);
        AddParameter(selectCmd, "@maxPartitions", maxPartitions);

        var candidates = new List<int>();
        await using (var reader = await selectCmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                candidates.Add(reader.GetInt32(0));
        }

        if (candidates.Count == 0)
            return Array.Empty<int>();

        // Step 2: atomically claim/renew each candidate.
        var owned = new List<int>(candidates.Count);
        foreach (var partitionNo in candidates)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            await using var updateCmd = connection.CreateCommand();
            updateCmd.Transaction = dbTransaction;
            updateCmd.CommandText = $"""
                UPDATE {fullTableName}
                SET "OwnerId"     = @ownerId,
                    "LockedUntil" = @lockedUntil,
                    "UpdatedAt"   = @now
                WHERE "WorkerName"   = @workerName
                  AND "PartitionNo"  = @partitionNo
                  AND (
                      "OwnerId" = @ownerId
                      OR "LockedUntil" IS NULL
                      OR "LockedUntil" < @now
                  );
                """;

            AddParameter(updateCmd, "@workerName",   workerName);
            AddParameter(updateCmd, "@partitionNo",  partitionNo);
            AddParameter(updateCmd, "@ownerId",      ownerId);
            AddParameter(updateCmd, "@lockedUntil",  lockedUntil);
            AddParameter(updateCmd, "@now",          now);

            var rows = await updateCmd.ExecuteNonQueryAsync(cancellationToken);
            if (rows > 0)
                owned.Add(partitionNo);
        }

        return owned;
    }

    /// <inheritdoc />
    public async Task ReleaseAllAsync(
        string workerName,
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var entityType = dbContext.Model.FindEntityType(typeof(PartitionLease))!;
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
              AND "OwnerId"    = @ownerId;
            """;

        AddParameter(command, "@workerName", workerName);
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
