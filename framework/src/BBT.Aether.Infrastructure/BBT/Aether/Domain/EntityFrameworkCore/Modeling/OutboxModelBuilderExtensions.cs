using BBT.Aether.Domain.Events;
using Microsoft.EntityFrameworkCore;

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
    /// <param name="schemaName">Schema name</param>
    /// <returns>The ModelBuilder for method chaining</returns>
    public static ModelBuilder ConfigureOutbox(this ModelBuilder builder, string? schemaName = null)
    {
        builder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("OutboxMessages", schemaName);

            entity.HasKey(e => e.Id);

            entity.Property(e => e.EventName)
                .IsRequired()
                .HasMaxLength(500);

            entity.Property(e => e.EventData)
                .IsRequired();

            entity.Property(e => e.CreatedAt)
                .IsRequired();

            entity.Property(e => e.ProcessedAt);

            entity.Property(e => e.RetryCount)
                .IsRequired()
                .HasDefaultValue(0);

            entity.Property(e => e.LastError)
                .HasMaxLength(4000);

            entity.Property(e => e.NextRetryAt);

            // Index for processing unprocessed messages
            entity.HasIndex(e => new { e.ProcessedAt, e.NextRetryAt, e.RetryCount })
                .HasDatabaseName("IX_OutboxMessages_Processing");

            // Index for cleanup of old processed messages
            entity.HasIndex(e => new { e.ProcessedAt, e.CreatedAt })
                .HasDatabaseName("IX_OutboxMessages_Cleanup");

            // Apply convention-based configuration (handles IHasExtraProperties automatically)
            entity.ConfigureByConvention();
        });

        return builder;
    }
}

