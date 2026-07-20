using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.Repositories;
using BBT.Aether.MultiSchema;
using BBT.Aether.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BBT.Aether.BackgroundJob;

/// <summary>
/// PostgreSQL-specific implementation of <see cref="IJobArmingLeaseStore"/> that uses
/// <c>FOR UPDATE SKIP LOCKED</c> to atomically claim a disjoint batch of due jobs. Under concurrent
/// access each pod receives a non-overlapping set of rows: if pod A holds a row lock, pod B's
/// <c>SKIP LOCKED</c> query skips that row entirely, so no two pods ever claim the same job.
/// </summary>
public class NpgsqlJobArmingLeaseStore<TDbContext>(
    IAetherDbContextProvider<TDbContext> dbContextProvider,
    ICurrentSchema currentSchema,
    IClock clock,
    BackgroundJobOptions? options) : IJobArmingLeaseStore
    where TDbContext : DbContext, IHasEfCoreBackgroundJobs
{
    /// <summary>
    /// Backward-compatible constructor that preserves the former ambient-schema behavior.
    /// </summary>
    public NpgsqlJobArmingLeaseStore(
        IAetherDbContextProvider<TDbContext> dbContextProvider,
        ICurrentSchema currentSchema,
        IClock clock)
        : this(dbContextProvider, currentSchema, clock, null)
    {
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<BackgroundJobArmingClaim>> ClaimBatchAsync(
        int batchSize,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        using var schemaScope = BeginConfiguredSchemaScope();
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var entityType = dbContext.Model.FindEntityType(typeof(BackgroundJobInfo))!;
        var schema = currentSchema.Name
            ?? throw new InvalidOperationException("Current schema is not set.");
        var fullTableName = PostgreSqlRelationName.For(entityType, schema);

        var connection = dbContext.Database.GetDbConnection();
        var now = clock.UtcNow;
        var armingUntil = now.Add(leaseDuration);
        var armingToken = Guid.NewGuid();

        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        var dbTransaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();

        await using var command = connection.CreateCommand();
        command.Transaction = dbTransaction;
        // workerId is not persisted to the row — it is available for logging/diagnostics only.
        command.CommandText = $"""
            UPDATE {fullTableName}
            SET
                "ArmingToken"  = @armingToken,
                "ArmingUntil"  = @armingUntil,
                "ModifiedAt"   = @now
            WHERE "Id" IN (
                SELECT "Id"
                FROM {fullTableName}
                WHERE ("Status" = @pending
                       OR ("Status" = @retrying
                           AND "NextRetryAt" IS NOT NULL
                           AND "NextRetryAt" <= @now))
                  AND ("ArmingToken" IS NULL OR "ArmingUntil" < @now)
                ORDER BY "NextRetryAt" NULLS FIRST, "Id"
                LIMIT @batchSize
                FOR UPDATE SKIP LOCKED
            )
            RETURNING "Id", "Status", "HandlerName", "JobName", "ExpressionValue", "Payload",
                      "RetryCount", "NextRetryAt", "Kind", "MaxRetryCount",
                      "ArmingToken", "ArmingUntil";
            """;

        AddParameter(command, "@armingToken", armingToken);
        AddParameter(command, "@armingUntil", armingUntil);
        AddParameter(command, "@pending",     (int)BackgroundJobStatus.Pending);
        AddParameter(command, "@retrying",    (int)BackgroundJobStatus.Retrying);
        AddParameter(command, "@now",         now);
        AddParameter(command, "@batchSize",   batchSize);

        var claims = new List<BackgroundJobArmingClaim>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id          = reader.GetGuid(reader.GetOrdinal("Id"));
            var status      = (BackgroundJobStatus)reader.GetInt32(reader.GetOrdinal("Status"));
            var handlerName = reader.GetString(reader.GetOrdinal("HandlerName"));
            var jobName     = reader.GetString(reader.GetOrdinal("JobName"));

            var job = new BackgroundJobInfo(id, handlerName, jobName)
            {
                ExpressionValue = reader.IsDBNull(reader.GetOrdinal("ExpressionValue"))
                    ? string.Empty
                    : reader.GetString(reader.GetOrdinal("ExpressionValue")),
                Payload       = ReadPayload(reader),
                Status        = status,
                RetryCount    = reader.GetInt32(reader.GetOrdinal("RetryCount")),
                NextRetryAt   = reader.IsDBNull(reader.GetOrdinal("NextRetryAt")) ? null : reader.GetDateTime(reader.GetOrdinal("NextRetryAt")),
                Kind          = (JobKind)reader.GetInt32(reader.GetOrdinal("Kind")),
                MaxRetryCount = reader.GetInt32(reader.GetOrdinal("MaxRetryCount")),
                ArmingToken   = reader.IsDBNull(reader.GetOrdinal("ArmingToken")) ? null : reader.GetGuid(reader.GetOrdinal("ArmingToken")),
                ArmingUntil   = reader.IsDBNull(reader.GetOrdinal("ArmingUntil")) ? null : reader.GetDateTime(reader.GetOrdinal("ArmingUntil")),
            };

            claims.Add(new BackgroundJobArmingClaim(job, status, armingToken));
        }

        return claims;
    }

    private IDisposable BeginConfiguredSchemaScope()
    {
        return string.IsNullOrWhiteSpace(options?.Schema)
            ? global::BBT.Aether.NullDisposable.Instance
            : currentSchema.Change(options.Schema);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }

    private static JsonElement ReadPayload(DbDataReader reader)
    {
        var ordinal = reader.GetOrdinal("Payload");
        if (reader.IsDBNull(ordinal))
            return JsonDocument.Parse("{}").RootElement.Clone();
        var json = reader.GetString(ordinal);
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
