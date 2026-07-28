using BBT.Aether.Domain.Events;
using BBT.Aether.Events;
using Microsoft.EntityFrameworkCore;
using InboxMessage = BBT.Aether.Domain.Events.InboxMessage;

namespace BBT.Aether.Domain.EntityFrameworkCore.Modeling;

/// <summary>
/// Extension methods for configuring the Inbox pattern entities.
/// </summary>
public static class InboxModelBuilderExtensions
{
    /// <summary>
    /// Configures the InboxMessage entity with appropriate table name, indexes, and constraints.
    /// </summary>
    /// <param name="builder">The ModelBuilder instance</param>
    /// <returns>The ModelBuilder for method chaining</returns>
    /// <remarks>
    /// The table is mapped without an explicit schema. Schema is resolved at runtime via
    /// <c>SET LOCAL search_path</c> by the UnitOfWork, so baking it into the EF model is avoided.
    /// </remarks>
    public static ModelBuilder ConfigureInbox(this ModelBuilder builder)
    {
        builder.Entity<InboxMessage>(entity =>
        {
            entity.ToTable("InboxMessages");

            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(e => e.EventName)
                .IsRequired()
                .HasMaxLength(500);

            entity.Property(e => e.EventData)
                .IsRequired();

            entity.Property(e => e.CreatedAt)
                .IsRequired();

            entity.Property(e => e.Status)
                .IsRequired()
                .HasConversion<int>();

            entity.Property(e => e.HandledTime);

            entity.Property(e => e.RetryCount)
                .IsRequired()
                .HasDefaultValue(0);

            entity.Property(e => e.NextRetryTime);

            entity.Property(e => e.LockedBy)
                .HasMaxLength(200);

            entity.Property(e => e.LockedUntil);

            entity.Property(e => e.PartitionId)
                .IsRequired()
                .HasDefaultValue((short)0);

            // Dispatch index: partial on the statuses the lease query can match. No INCLUDE:
            // the lease query ends in FOR UPDATE SKIP LOCKED, which must lock the heap tuple
            // and so cannot be served by an index-only scan regardless of included columns.
            entity.HasIndex(e => new { e.PartitionId, e.NextRetryTime, e.CreatedAt })
                .HasDatabaseName("IX_InboxMessages_Dispatch")
                .HasFilter($"\"Status\" IN ({(int)IncomingEventStatus.Pending}, {(int)IncomingEventStatus.Processing})");

            // Retention index: serves the retention/cleanup deletion of old handled messages.
            // Distinct name from the legacy non-partial cleanup index it replaces -- this is a
            // genuinely different index (partial, single-column), not a like-for-like rebuild.
            entity.HasIndex(e => new { e.HandledTime })
                .HasDatabaseName("IX_InboxMessages_Retention")
                .HasFilter($"\"Status\" = {(int)IncomingEventStatus.Processed}");

            // Apply convention-based configuration (handles IHasExtraProperties automatically)
            entity.ConfigureByConvention();
        });

        return builder;
    }
}

