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

            entity.Property(e => e.PartitionId)
                .IsRequired()
                .HasDefaultValue((short)0);

            entity.HasIndex(e => new { e.PartitionId, e.NextRetryTime, e.CreatedAt })
                .HasDatabaseName("IX_InboxMessages_Dispatch")
                .IncludeProperties(e => new { e.LockedUntil })
                .HasFilter("\"Status\" IN (0, 1)");

            entity.HasIndex(e => new { e.HandledTime })
                .HasDatabaseName("IX_InboxMessages_Cleanup")
                .HasFilter("\"Status\" = 2");

            // Apply convention-based configuration (handles IHasExtraProperties automatically)
            entity.ConfigureByConvention();
        });

        return builder;
    }
}

