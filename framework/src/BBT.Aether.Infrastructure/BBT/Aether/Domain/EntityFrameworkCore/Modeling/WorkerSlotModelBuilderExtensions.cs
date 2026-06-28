using BBT.Aether.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Domain.EntityFrameworkCore.Modeling;

/// <summary>
/// Extension methods for configuring the <see cref="WorkerSlot"/> entity.
/// </summary>
public static class WorkerSlotModelBuilderExtensions
{
    /// <summary>
    /// Configures the WorkerSlot entity. The table is placed in the given schema (e.g. "sys_queues").
    /// </summary>
    public static ModelBuilder ConfigureWorkerSlots(this ModelBuilder builder, string? schema = null)
    {
        builder.Entity<WorkerSlot>(entity =>
        {
            entity.ToTable("worker_slots", schema);

            entity.HasKey(e => new { e.WorkerName, e.SlotNo });

            entity.Property(e => e.WorkerName)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(e => e.SlotNo)
                .IsRequired();

            entity.Property(e => e.OwnerId)
                .HasMaxLength(500);

            entity.Property(e => e.LockedUntil);

            entity.Property(e => e.IsEnabled)
                .IsRequired()
                .HasDefaultValue(true);

            entity.Property(e => e.UpdatedAt)
                .IsRequired();
        });

        return builder;
    }
}
