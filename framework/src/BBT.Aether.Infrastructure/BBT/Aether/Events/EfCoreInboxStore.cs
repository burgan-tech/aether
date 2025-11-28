using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BBT.Aether.Events;

/// <summary>
/// Entity Framework Core implementation of the inbox store.
/// </summary>
public class EfCoreInboxStore<TDbContext>(
    TDbContext dbContext,
    IEventSerializer eventSerializer,
    IClock clock) : IInboxStore
    where TDbContext : DbContext, IHasEfCoreInbox
{
    public async Task<bool> HasProcessedAsync(string eventId, CancellationToken cancellationToken = default)
    {
        return await dbContext.InboxMessages
            .AnyAsync(m => m.Id == eventId && m.Status == IncomingEventStatus.Processed, cancellationToken);
    }

    public async Task MarkAsProcessedAsync(string eventId, CancellationToken cancellationToken = default)
    {
        var message = await dbContext.InboxMessages
            .FirstOrDefaultAsync(m => m.Id == eventId, cancellationToken);

        if (message != null)
        {
            message.MarkAsProcessed(clock.UtcNow);
        }
    }

    public async Task StorePendingAsync(CloudEventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;

        // Serialize CloudEventEnvelope to bytes
        var serializedBytes = eventSerializer.Serialize(envelope);

        // Store EventName (Type) for handler resolution
        var inboxMessage = new Domain.Events.InboxMessage(envelope.Id, envelope.Type, serializedBytes)
        {
            CreatedAt = now,
            Status = IncomingEventStatus.Pending
        };

        // Store metadata in ExtraProperties
        if (envelope.Topic != null)
        {
            inboxMessage.ExtraProperties["Topic"] = envelope.Topic;
        }

        if (envelope.Version.HasValue)
        {
            inboxMessage.ExtraProperties["Version"] = envelope.Version.Value;
        }

        if (envelope.Source != null)
        {
            inboxMessage.ExtraProperties["Source"] = envelope.Source;
        }

        if (envelope.Subject != null)
        {
            inboxMessage.ExtraProperties["Subject"] = envelope.Subject;
        }

        await dbContext.InboxMessages.AddAsync(inboxMessage, cancellationToken);
        // SaveChanges removed - will be flushed by UoW Commit or calling code
    }

    public async Task<List<InboxMessage>> GetPendingEventsAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        var domainMessages = await dbContext.InboxMessages
            .Where(m => m.Status == IncomingEventStatus.Pending)
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        // Convert Domain.Events.InboxMessage to Abstractions.InboxMessage
        return domainMessages.Select(dm => new InboxMessage
        {
            Id = dm.Id,
            EventName = dm.EventName,
            EventData = dm.EventData,
            CreatedAt = dm.CreatedAt,
            Status = dm.Status,
            HandledTime = dm.HandledTime,
            RetryCount = dm.RetryCount,
            NextRetryTime = dm.NextRetryTime,
            LockedBy = dm.LockedBy,
            LockedUntil = dm.LockedUntil,
            ExtraProperties = dm.ExtraProperties.ToDictionary(kvp => kvp.Key, kvp => kvp.Value!)
        }).ToList();
    }

    public async Task MarkAsProcessingAsync(string eventId, CancellationToken cancellationToken = default)
    {
        var message = await dbContext.InboxMessages
            .FirstOrDefaultAsync(m => m.Id == eventId, cancellationToken);

        if (message != null)
        {
            message.MarkAsProcessing();
        }
    }

    public async Task MarkAsFailedAsync(string eventId, CancellationToken cancellationToken = default)
    {
        var message = await dbContext.InboxMessages
            .FirstOrDefaultAsync(m => m.Id == eventId, cancellationToken);

        if (message != null)
        {
            message.MarkAsDiscarded(clock.UtcNow);
        }
    }

    public async Task<int> CleanupOldMessagesAsync(int batchSize, TimeSpan retentionPeriod,
        CancellationToken cancellationToken = default)
    {
        var cutoffDate = clock.UtcNow - retentionPeriod;

        var oldMessages = await dbContext.InboxMessages
            .Where(m => m.Status == IncomingEventStatus.Processed &&
                        m.HandledTime != null &&
                        m.HandledTime < cutoffDate)
            .OrderBy(m => m.HandledTime)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        if (!oldMessages.Any())
            return 0;

        dbContext.InboxMessages.RemoveRange(oldMessages);
        return await dbContext.SaveChangesAsync(cancellationToken);
    }

    public virtual async Task<IReadOnlyList<InboxMessage>> LeaseBatchAsync(
        int batchSize,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
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
                                    AND ("LockedUntil" IS NULL OR "LockedUntil" < @now)
                                    AND ("NextRetryTime" IS NULL OR "NextRetryTime" <= @now)
                                  ORDER BY "CreatedAt"
                                  LIMIT @batchSize
                                  FOR UPDATE SKIP LOCKED
                              )
                              RETURNING "Id", "Status", "EventName", "EventData", "CreatedAt",
                                        "HandledTime", "LockedBy", "LockedUntil", "RetryCount", "NextRetryTime";
                              """;

        AddParameter(command, "@processing", (int)IncomingEventStatus.Processing);
        AddParameter(command, "@pending", (int)IncomingEventStatus.Pending);
        AddParameter(command, "@workerId", workerId);
        AddParameter(command, "@lockedUntil", lockedUntil);
        AddParameter(command, "@now", now);
        AddParameter(command, "@batchSize", batchSize);

        var messages = new List<InboxMessage>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var message = new InboxMessage
            {
                Id = reader.GetString(reader.GetOrdinal("Id")),
                Status = (IncomingEventStatus)reader.GetInt32(reader.GetOrdinal("Status")),
                EventName = reader.GetString(reader.GetOrdinal("EventName")),
                EventData = (byte[])reader["EventData"],
                CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
                HandledTime = reader.IsDBNull(reader.GetOrdinal("HandledTime")) 
                    ? null 
                    : reader.GetDateTime(reader.GetOrdinal("HandledTime")),
                LockedBy = reader.IsDBNull(reader.GetOrdinal("LockedBy")) 
                    ? null 
                    : reader.GetString(reader.GetOrdinal("LockedBy")),
                LockedUntil = reader.IsDBNull(reader.GetOrdinal("LockedUntil")) 
                    ? null 
                    : reader.GetDateTime(reader.GetOrdinal("LockedUntil")),
                RetryCount = reader.GetInt32(reader.GetOrdinal("RetryCount")),
                NextRetryTime = reader.IsDBNull(reader.GetOrdinal("NextRetryTime")) 
                    ? null 
                    : reader.GetDateTime(reader.GetOrdinal("NextRetryTime")),
                ExtraProperties = new Dictionary<string, object>()
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