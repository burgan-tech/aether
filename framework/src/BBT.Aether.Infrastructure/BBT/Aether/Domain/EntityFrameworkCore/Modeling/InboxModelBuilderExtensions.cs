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

            // Index for processing pending messages
            entity.HasIndex(e => new { e.Status, e.NextRetryTime, e.RetryCount })
                .HasDatabaseName("IX_InboxMessages_Processing");

            // Index for cleanup of old processed messages
            entity.HasIndex(e => new { e.Status, e.HandledTime })
                .HasDatabaseName("IX_InboxMessages_Cleanup");

            // Apply convention-based configuration (handles IHasExtraProperties automatically)
            entity.ConfigureByConvention();
        });

        return builder;
    }
}

