using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.BackgroundJob;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BBT.Aether.Npgsql.BackgroundJob;

/// <summary>
/// PostgreSQL-specific implementation of <see cref="IWorkerSettingsStore"/>.
/// Reads and writes the <c>worker_settings</c> table via raw SQL, and reconciles
/// the <c>worker_slots</c> table using upsert + update in two lightweight queries.
/// </summary>
public class NpgsqlWorkerSettingsStore<TDbContext>(
    IAetherDbContextProvider<TDbContext> dbContextProvider)
    : IWorkerSettingsStore
    where TDbContext : DbContext, IHasEfCoreWorkerSettings, IHasEfCoreWorkerSlots
{
    /// <inheritdoc />
    public async Task<WorkerSettings?> GetAsync(string workerName, CancellationToken ct = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(ct);
        var (settingsTable, slotTable) = GetFullTableNames(dbContext);
        var connection = dbContext.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        var dbTransaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();

        await using var command = connection.CreateCommand();
        command.Transaction = dbTransaction;
        command.CommandText = $"""
            SELECT "WorkerName", "DesiredSlotCount", "MinSlotCount", "MaxSlotCount", "UpdatedAt", "UpdatedBy"
            FROM {settingsTable}
            WHERE "WorkerName" = @workerName;
            """;

        AddParameter(command, "@workerName", workerName);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new WorkerSettings
        {
            WorkerName = reader.GetString(0),
            DesiredSlotCount = reader.GetInt32(1),
            MinSlotCount = reader.GetInt32(2),
            MaxSlotCount = reader.GetInt32(3),
            UpdatedAt = reader.GetDateTime(4),
            UpdatedBy = reader.IsDBNull(5) ? null : reader.GetString(5)
        };
    }

    /// <inheritdoc />
    public async Task<WorkerSettings> GetOrDefaultAsync(
        string workerName,
        int defaultDesiredSlotCount,
        CancellationToken ct = default)
    {
        var settings = await GetAsync(workerName, ct);
        return settings ?? new WorkerSettings
        {
            WorkerName = workerName,
            DesiredSlotCount = defaultDesiredSlotCount,
            MinSlotCount = 0,
            MaxSlotCount = defaultDesiredSlotCount,
            UpdatedAt = DateTime.UtcNow
        };
    }

    /// <inheritdoc />
    public async Task UpdateDesiredSlotCountAsync(
        string workerName,
        int desiredSlotCount,
        string? updatedBy,
        CancellationToken ct = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(ct);
        var (settingsTable, _) = GetFullTableNames(dbContext);
        var connection = dbContext.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        var dbTransaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();

        await using var command = connection.CreateCommand();
        command.Transaction = dbTransaction;
        command.CommandText = $"""
            UPDATE {settingsTable}
            SET "DesiredSlotCount" = @desiredSlotCount,
                "UpdatedAt"        = @now,
                "UpdatedBy"        = @updatedBy
            WHERE "WorkerName" = @workerName;
            """;

        AddParameter(command, "@workerName",       workerName);
        AddParameter(command, "@desiredSlotCount", desiredSlotCount);
        AddParameter(command, "@now",              DateTime.UtcNow);
        AddParameter(command, "@updatedBy",        (object?)updatedBy ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task ReconcileSlotsAsync(
        string workerName,
        int desiredSlotCount,
        int maxSlotCount,
        CancellationToken ct = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(ct);
        var (_, slotTable) = GetFullTableNames(dbContext);
        var connection = dbContext.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        var dbTransaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();

        // Enable / insert slots 0..desired-1
        await using (var enableCmd = connection.CreateCommand())
        {
            enableCmd.Transaction = dbTransaction;
            enableCmd.CommandText = $"""
                INSERT INTO {slotTable} ("WorkerName", "SlotNo", "IsEnabled", "UpdatedAt")
                SELECT @workerName, s, true, now()
                FROM generate_series(0, @desired - 1) s
                ON CONFLICT ("WorkerName", "SlotNo")
                DO UPDATE SET "IsEnabled"  = true,
                              "UpdatedAt"  = now()
                WHERE {slotTable}."IsEnabled" = false;
                """;

            AddParameter(enableCmd, "@workerName", workerName);
            AddParameter(enableCmd, "@desired",    desiredSlotCount);

            await enableCmd.ExecuteNonQueryAsync(ct);
        }

        // Disable slots >= desired (never delete; the owning pod may still be running)
        await using (var disableCmd = connection.CreateCommand())
        {
            disableCmd.Transaction = dbTransaction;
            disableCmd.CommandText = $"""
                UPDATE {slotTable}
                SET "IsEnabled"  = false,
                    "UpdatedAt"  = now()
                WHERE "WorkerName" = @workerName
                  AND "SlotNo"    >= @desired
                  AND "IsEnabled"  = true;
                """;

            AddParameter(disableCmd, "@workerName", workerName);
            AddParameter(disableCmd, "@desired",    desiredSlotCount);

            await disableCmd.ExecuteNonQueryAsync(ct);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (string settingsTable, string slotTable) GetFullTableNames(TDbContext dbContext)
    {
        var settingsEntity = dbContext.Model.FindEntityType(typeof(WorkerSettings))!;
        var slotEntity     = dbContext.Model.FindEntityType(typeof(WorkerSlot))!;

        return (FullName(settingsEntity.GetTableName(), settingsEntity.GetSchema()),
                FullName(slotEntity.GetTableName(),     slotEntity.GetSchema()));

        static string FullName(string? table, string? schema)
            => string.IsNullOrEmpty(schema) ? $"\"{table}\"" : $"\"{schema}\".\"{table}\"";
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }
}
