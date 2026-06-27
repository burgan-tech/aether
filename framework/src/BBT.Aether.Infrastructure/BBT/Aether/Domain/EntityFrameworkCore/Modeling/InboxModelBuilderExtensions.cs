using BBT.Aether.Domain.Events;
using Microsoft.EntityFrameworkCore;

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

            entity.Property(e => e.PartitionNo)
                .IsRequired()
                .HasDefaultValue(0);

            // Index for processing pending messages with lease support
            entity.HasIndex(e => new { e.Status, e.LockedUntil, e.NextRetryTime, e.CreatedAt })
                .HasDatabaseName("IX_InboxMessages_Processing");

            // Composite index for partition-aware lease queries (used when PartitioningEnabled = true)
            entity.HasIndex(e => new { e.PartitionNo, e.Status, e.LockedUntil, e.NextRetryTime })
                .HasDatabaseName("IX_InboxMessages_Claim");

            // Index for cleanup of old processed messages
            entity.HasIndex(e => new { e.Status, e.HandledTime })
                .HasDatabaseName("IX_InboxMessages_Cleanup");

            // Apply convention-based configuration (handles IHasExtraProperties automatically)
            entity.ConfigureByConvention();
        });

        return builder;
    }
}

