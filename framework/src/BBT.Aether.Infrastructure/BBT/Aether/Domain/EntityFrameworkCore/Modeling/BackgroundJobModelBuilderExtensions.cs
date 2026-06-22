using BBT.Aether.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Domain.EntityFrameworkCore.Modeling;

/// <summary>
/// Extension methods for configuring the Background Job pattern entities.
/// </summary>
public static class BackgroundJobModelBuilderExtensions
{
    /// <summary>
    /// Configures the BackgroundJobInfo entity with appropriate table name, indexes, and constraints.
    /// </summary>
    /// <param name="builder">The ModelBuilder instance</param>
    /// <returns>The ModelBuilder for method chaining</returns>
    /// <remarks>
    /// The table is mapped without an explicit schema. Schema is resolved at runtime via
    /// <c>SET LOCAL search_path</c> by the UnitOfWork, so baking it into the EF model is avoided.
    /// </remarks>
    public static ModelBuilder ConfigureBackgroundJob(this ModelBuilder builder)
    {
        builder.Entity<BackgroundJobInfo>(entity =>
        {
            entity.ToTable("BackgroundJobs");

            entity.HasKey(e => e.Id);

            entity.Property(e => e.HandlerName)
                .IsRequired()
                .HasMaxLength(500);

            entity.Property(e => e.JobName)
                .IsRequired()
                .HasMaxLength(500);

            entity.Property(e => e.ExpressionValue)
                .HasMaxLength(1000);

            entity.Property(e => e.Payload)
                .IsRequired()
                .HasColumnType("jsonb"); // PostgreSQL; use "nvarchar(max)" for SQL Server

            entity.Property(e => e.Status)
                .IsRequired()
                .HasConversion<int>();

            entity.Property(e => e.HandledTime);

            entity.Property(e => e.RetryCount)
                .IsRequired()
                .HasDefaultValue(0);

            entity.Property(e => e.LastError)
                .HasMaxLength(4000);

            entity.Property(e => e.Kind)
                .HasConversion<int>();

            entity.Property(e => e.MaxRetryCount);

            entity.Property(e => e.NextRetryAt);

            entity.Property(e => e.LastRunAt);

            entity.Property(e => e.RunningSince);

            // Index for the arming poller (query by status + next due time)
            entity.HasIndex(e => new { e.Status, e.NextRetryAt })
                .HasDatabaseName("IX_BackgroundJobs_Arming");

            // Index for the visibility-timeout reaper (query Running jobs by claim time)
            entity.HasIndex(e => new { e.Status, e.RunningSince })
                .HasDatabaseName("IX_BackgroundJobs_Running");

            // Index for processing jobs (query by status)
            entity.HasIndex(e => new { e.Status, e.HandledTime })
                .HasDatabaseName("IX_BackgroundJobs_Processing");

            // Index for finding jobs by JobName (external scheduler identifier)
            entity.HasIndex(e => e.JobName)
                .HasDatabaseName("IX_BackgroundJobs_JobName");

            // Index for finding jobs by HandlerName and Status
            entity.HasIndex(e => new { e.HandlerName, e.Status })
                .HasDatabaseName("IX_BackgroundJobs_HandlerName_Status");

            // Apply convention-based configuration (handles IHasExtraProperties automatically)
            entity.ConfigureByConvention();
        });

        return builder;
    }
}

