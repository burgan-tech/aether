using BBT.Aether.Domain.Events;
using BBT.Aether.Events;
using Microsoft.EntityFrameworkCore;
using OutboxMessage = BBT.Aether.Domain.Events.OutboxMessage;

namespace BBT.Aether.Domain.EntityFrameworkCore.Modeling;

/// <summary>
/// Extension methods for configuring the Outbox pattern entities.
/// </summary>
public static class OutboxModelBuilderExtensions
{
    /// <summary>
    /// Configures the OutboxMessage entity with appropriate table name, indexes, and constraints.
    /// </summary>
    /// <param name="builder">The ModelBuilder instance</param>
    /// <returns>The ModelBuilder for method chaining</returns>
    /// <remarks>
    /// The table is mapped without an explicit schema. Schema is resolved at runtime via
    /// <c>SET LOCAL search_path</c> by the UnitOfWork, so baking it into the EF model is avoided.
    /// </remarks>
    public static ModelBuilder ConfigureOutbox(this ModelBuilder builder)
    {
        builder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("OutboxMessages");

            entity.HasKey(e => e.Id);

            entity.Property(e => e.EventName)
                .IsRequired()
                .HasMaxLength(500);

            entity.Property(e => e.EventData)
                .IsRequired();

            entity.Property(e => e.Status)
                .IsRequired()
                .HasConversion<int>()
                .HasDefaultValue(OutboxMessageStatus.Pending);

            entity.Property(e => e.CreatedAt)
                .IsRequired();

            entity.Property(e => e.ProcessedAt);

            entity.Property(e => e.RetryCount)
                .IsRequired()
                .HasDefaultValue(0);

            entity.Property(e => e.LastError)
                .HasMaxLength(4000);

            entity.Property(e => e.NextRetryAt);

            entity.Property(e => e.LockedBy)
                .HasMaxLength(200);

            entity.Property(e => e.LockedUntil);

            entity.Property(e => e.PartitionNo)
                .IsRequired()
                .HasDefaultValue(0);

            // Index for processing pending messages with lease support
            entity.HasIndex(e => new { e.Status, e.LockedUntil, e.NextRetryAt, e.CreatedAt })
                .HasDatabaseName("IX_OutboxMessages_Processing");

            // Composite index for partition-aware lease queries (used when PartitioningEnabled = true)
            entity.HasIndex(e => new { e.PartitionNo, e.Status, e.LockedUntil, e.NextRetryAt })
                .HasDatabaseName("IX_OutboxMessages_Claim");

            // Index for cleanup of old processed messages
            entity.HasIndex(e => new { e.ProcessedAt, e.CreatedAt })
                .HasDatabaseName("IX_OutboxMessages_Cleanup");

            // Apply convention-based configuration (handles IHasExtraProperties automatically)
            entity.ConfigureByConvention();
        });

        return builder;
    }
}

