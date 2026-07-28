using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Guids;
using BBT.Aether.MultiSchema;
using BBT.Aether.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Events;

/// <summary>
/// Entity Framework Core implementation of the outbox store.
/// </summary>
public class EfCoreOutboxStore<TDbContext>(
    IAetherDbContextProvider<TDbContext> dbContextProvider,
    IEventSerializer eventSerializer,
    IGuidGenerator guidGenerator,
    IClock clock,
    AetherOutboxOptions options,
    IOutboxSignalCollector signalCollector,
    ICurrentSchema? currentSchema) : IOutboxStore
    where TDbContext : DbContext, IHasEfCoreOutbox
{
    /// <summary>
    /// Backward-compatible constructor that preserves the former ambient-schema behavior.
    /// </summary>
    public EfCoreOutboxStore(
        IAetherDbContextProvider<TDbContext> dbContextProvider,
        IEventSerializer eventSerializer,
        IGuidGenerator guidGenerator,
        IClock clock)
        : this(
            dbContextProvider,
            eventSerializer,
            guidGenerator,
            clock,
            new AetherOutboxOptions { Schema = null },
            new NullOutboxSignalCollector(),
            null)
    {
    }

    public async Task StoreAsync(CloudEventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        using var schemaScope = BeginConfiguredSchemaScope();
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var serializedBytes = eventSerializer.Serialize(envelope);

        var outboxMessage = new Domain.Events.OutboxMessage(guidGenerator.Create(), envelope.Type, serializedBytes)
        {
            CreatedAt = clock.UtcNow,
            RetryCount = 0,
            Status = OutboxMessageStatus.Pending,
            PartitionId = MessagePartitionResolver.Resolve(
                envelope.Subject ?? envelope.Id, options.PartitionCount),
            ExtraProperties = { ["TopicName"] = envelope.Topic ?? envelope.Type }
        };

        if (envelope.Version.HasValue)
            outboxMessage.ExtraProperties["Version"] = envelope.Version.Value;
        if (envelope.Source != null)
            outboxMessage.ExtraProperties["Source"] = envelope.Source;
        if (envelope.Subject != null)
            outboxMessage.ExtraProperties["Subject"] = envelope.Subject;

        await dbContext.OutboxMessages.AddAsync(outboxMessage, cancellationToken);

        // Mirrors BeginConfiguredSchemaScope's precedence: options.Schema only takes effect when
        // currentSchema is available to apply it; otherwise the row lands wherever the ambient
        // schema (if any) already points, not at options.Schema.
        signalCollector.Mark(
            currentSchema is not null && !string.IsNullOrWhiteSpace(options.Schema)
                ? options.Schema
                : currentSchema?.Name ?? string.Empty,
            outboxMessage.PartitionId);
    }

    private IDisposable BeginConfiguredSchemaScope()
    {
        return currentSchema is null || string.IsNullOrWhiteSpace(options.Schema)
            ? global::BBT.Aether.NullDisposable.Instance
            : currentSchema.Change(options.Schema);
    }
}
