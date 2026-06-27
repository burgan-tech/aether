using BBT.Aether.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Domain.EntityFrameworkCore.Modeling;

/// <summary>
/// Extension methods for configuring the <see cref="PartitionLease"/> entity.
/// </summary>
public static class PartitionLeaseModelBuilderExtensions
{
    /// <summary>
    /// Configures the PartitionLease entity. The table is placed in the given schema (e.g. "sys_queues").
    /// </summary>
    public static ModelBuilder ConfigurePartitionLeases(this ModelBuilder builder, string? schema = null)
    {
        builder.Entity<PartitionLease>(entity =>
        {
            entity.ToTable("partition_leases", schema);

            entity.HasKey(e => new { e.WorkerName, e.PartitionNo });

            entity.Property(e => e.WorkerName)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(e => e.PartitionNo)
                .IsRequired();

            entity.Property(e => e.OwnerId)
                .HasMaxLength(500);

            entity.Property(e => e.LockedUntil);

            entity.Property(e => e.UpdatedAt)
                .IsRequired();
        });

        return builder;
    }
}
