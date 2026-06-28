using BBT.Aether.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Domain.EntityFrameworkCore.Modeling;

/// <summary>
/// Extension methods for configuring the <see cref="WorkerSettings"/> entity.
/// </summary>
public static class WorkerSettingsModelBuilderExtensions
{
    /// <summary>
    /// Configures the WorkerSettings entity. The table is placed in the given schema (e.g. "sys_queues").
    /// </summary>
    public static ModelBuilder ConfigureWorkerSettings(this ModelBuilder builder, string? schema = null)
    {
        builder.Entity<WorkerSettings>(entity =>
        {
            entity.ToTable("worker_settings", schema);

            entity.HasKey(e => e.WorkerName);

            entity.Property(e => e.WorkerName)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(e => e.DesiredSlotCount)
                .IsRequired();

            entity.Property(e => e.MinSlotCount)
                .IsRequired()
                .HasDefaultValue(0);

            entity.Property(e => e.MaxSlotCount)
                .IsRequired()
                .HasDefaultValue(20);

            entity.Property(e => e.UpdatedAt)
                .IsRequired();

            entity.Property(e => e.UpdatedBy)
                .HasMaxLength(500);
        });

        return builder;
    }
}
