using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Guids;
using BBT.Aether.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BBT.Aether.Events;

/// <summary>
/// Entity Framework Core implementation of the outbox store.
/// </summary>
public class EfCoreOutboxStore<TDbContext>(
    TDbContext dbContext,
    IEventSerializer eventSerializer,
    IGuidGenerator guidGenerator,
    IClock clock) : IOutboxStore
    where TDbContext : DbContext, IHasEfCoreOutbox
{
    public async Task StoreAsync(CloudEventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        // Serialize CloudEventEnvelope to bytes
        var serializedBytes = eventSerializer.Serialize(envelope);

        // Store EventName (Type) for handler resolution
        var outboxMessage = new Domain.Events.OutboxMessage(guidGenerator.Create(), envelope.Type, serializedBytes)
        {
            CreatedAt = clock.UtcNow,
            RetryCount = 0,
            Status = OutboxMessageStatus.Pending,
            ExtraProperties = {
                // Store full topic name for routing to the message broker
                ["TopicName"] = envelope.Topic ?? envelope.Type }
        };

        if (envelope.Version.HasValue)
        {
            outboxMessage.ExtraProperties["Version"] = envelope.Version.Value;
        }

        if (envelope.Source != null)
        {
            outboxMessage.ExtraProperties["Source"] = envelope.Source;
        }
        if (envelope.Subject != null)
        {
            outboxMessage.ExtraProperties["Subject"] = envelope.Subject;
        }

        await dbContext.OutboxMessages.AddAsync(outboxMessage, cancellationToken);
        // SaveChanges removed - will be flushed by UoW Commit or calling code
    }

    public virtual async Task<IReadOnlyList<OutboxMessage>> LeaseBatchAsync(
        int batchSize,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var entityType = dbContext.Model.FindEntityType(typeof(BBT.Aether.Domain.Events.OutboxMessage))!;
        var tableName = entityType.GetTableName();
        var schema = entityType.GetSchema();

        var fullTableName = string.IsNullOrEmpty(schema)
            ? $"\"{tableName}\""
            : $"\"{schema}\".\"{tableName}\"";
        
        var connection = dbContext.Database.GetDbConnection();
        var now = clock.UtcNow;
        var lockedUntil = now.Add(leaseDuration);

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();

        // PostgreSQL-specific query with FOR UPDATE SKIP LOCKED
        // Virtual method allows developers to override for other database providers
        command.CommandText = $"""
                              UPDATE {fullTableName}
                              SET 
                                  "Status" = @processing,
                                  "LockedBy" = @workerId,
                                  "LockedUntil" = @lockedUntil
                              WHERE "Id" IN (
                                  SELECT "Id"
                                  FROM {fullTableName}
                                  WHERE "Status" = @pending
                                    AND "RetryCount" < 10
                                    AND ("LockedUntil" IS NULL OR "LockedUntil" < @now)
                                    AND ("NextRetryAt" IS NULL OR "NextRetryAt" <= @now)
                                  ORDER BY "CreatedAt"
                                  LIMIT @batchSize
                                  FOR UPDATE SKIP LOCKED
                              )
                              RETURNING "Id", "Status", "EventName", "EventData", "CreatedAt",
                                        "ProcessedAt", "LockedBy", "LockedUntil", "LastError", "RetryCount", "NextRetryAt";
                              """;

        AddParameter(command, "@processing", (int)OutboxMessageStatus.Processing);
        AddParameter(command, "@pending", (int)OutboxMessageStatus.Pending);
        AddParameter(command, "@workerId", workerId);
        AddParameter(command, "@lockedUntil", lockedUntil);
        AddParameter(command, "@now", now);
        AddParameter(command, "@batchSize", batchSize);

        var messages = new List<OutboxMessage>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var message = new OutboxMessage
            {
                Id = reader.GetGuid(reader.GetOrdinal("Id")),
                Status = (OutboxMessageStatus)reader.GetInt32(reader.GetOrdinal("Status")),
                EventName = reader.GetString(reader.GetOrdinal("EventName")),
                EventData = (byte[])reader["EventData"],
                CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
                ProcessedAt = reader.IsDBNull(reader.GetOrdinal("ProcessedAt")) 
                    ? null 
                    : reader.GetDateTime(reader.GetOrdinal("ProcessedAt")),
                LockedBy = reader.IsDBNull(reader.GetOrdinal("LockedBy")) 
                    ? null 
                    : reader.GetString(reader.GetOrdinal("LockedBy")),
                LockedUntil = reader.IsDBNull(reader.GetOrdinal("LockedUntil")) 
                    ? null 
                    : reader.GetDateTime(reader.GetOrdinal("LockedUntil")),
                LastError = reader.IsDBNull(reader.GetOrdinal("LastError")) 
                    ? null 
                    : reader.GetString(reader.GetOrdinal("LastError")),
                RetryCount = reader.GetInt32(reader.GetOrdinal("RetryCount")),
                NextRetryAt = reader.IsDBNull(reader.GetOrdinal("NextRetryAt")) 
                    ? null 
                    : reader.GetDateTime(reader.GetOrdinal("NextRetryAt"))
            };

            messages.Add(message);
        }

        return messages;
    }

    protected virtual void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}

