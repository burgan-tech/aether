using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.Persistence;
using BBT.Aether.TestSupport;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace BBT.Aether.BackgroundJob;

public class EfCoreJobStoreTests
{
    private sealed class TestJobDbContext(DbContextOptions<TestJobDbContext> options)
        : DbContext(options), IHasEfCoreBackgroundJobs
    {
        public DbSet<BackgroundJobInfo> BackgroundJobs { get; set; } = default!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ConfigureBackgroundJob();
    }

    private static TestJobDbContext NewContext() =>
        new(new DbContextOptionsBuilder<TestJobDbContext>()
            .UseInMemoryDatabase($"jobs-{Guid.NewGuid()}")
            .Options);

    private static BackgroundJobInfo NewJob(Guid id, string jobName, BackgroundJobStatus status,
        string expression, int payloadVersion) =>
        new(id, "handler", jobName)
        {
            Status = status,
            ExpressionValue = expression,
            Payload = JsonDocument.Parse($"{{\"v\":{payloadVersion}}}").RootElement
        };

    /// <summary>
    /// Regression: a re-enqueue for the same JobName produces a NEW Guid. The update path must
    /// copy mutable fields onto the existing tracked entity WITHOUT touching its key, otherwise
    /// EF throws "The property 'BackgroundJobInfo.Id' is part of a key and so cannot be modified".
    /// </summary>
    [Fact]
    public async Task SaveAsync_WhenJobWithSameNameExists_UpdatesMutableFieldsAndPreservesKey()
    {
        await using var db = NewContext();

        var existingId = Guid.NewGuid();
        db.BackgroundJobs.Add(NewJob(existingId, "job-1", BackgroundJobStatus.Scheduled, "old", 1));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear(); // simulate a fresh request scope / load

        var store = new EfCoreJobStore<TestJobDbContext>(new FixedDbContextProvider<TestJobDbContext>(db));

        // Re-enqueue with a DIFFERENT Guid but the SAME JobName (the original failure scenario).
        var incoming = NewJob(Guid.NewGuid(), "job-1", BackgroundJobStatus.Running, "new", 2);

        await store.SaveAsync(incoming);
        await Should.NotThrowAsync(db.SaveChangesAsync()); // previously threw on key modification

        var rows = await db.BackgroundJobs.ToListAsync();
        var row = rows.ShouldHaveSingleItem();
        row.Id.ShouldBe(existingId); // key preserved, not overwritten by incoming.Id
        row.Status.ShouldBe(BackgroundJobStatus.Running);
        row.ExpressionValue.ShouldBe("new");
        row.ModifiedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task SaveAsync_WhenNoExistingJob_InsertsNewRow()
    {
        await using var db = NewContext();
        var store = new EfCoreJobStore<TestJobDbContext>(new FixedDbContextProvider<TestJobDbContext>(db));

        var id = Guid.NewGuid();
        await store.SaveAsync(NewJob(id, "job-new", BackgroundJobStatus.Scheduled, "expr", 1));
        await db.SaveChangesAsync();

        var row = (await db.BackgroundJobs.ToListAsync()).ShouldHaveSingleItem();
        row.Id.ShouldBe(id);
        row.JobName.ShouldBe("job-new");
    }

    [Fact]
    public async Task SaveAsync_RoundTripsKindRetryAndArmingFields()
    {
        await using var db = NewContext();
        var store = new EfCoreJobStore<TestJobDbContext>(new FixedDbContextProvider<TestJobDbContext>(db));

        var id = Guid.NewGuid();
        var job = new BackgroundJobInfo(id, "handler", "job-rt")
        {
            ExpressionValue = "0 * * * *",
            Payload = JsonDocument.Parse("{\"v\":1}").RootElement
        };

        // Defaults set by the public ctor.
        job.Status.ShouldBe(BackgroundJobStatus.Pending);
        job.Kind.ShouldBe(JobKind.OneShot);
        job.NextRetryAt.ShouldNotBeNull();

        // Set the round-trip fields explicitly.
        job.Kind = JobKind.Recurring;
        job.MaxRetryCount = 5;
        var nextRetryAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var lastRunAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        job.NextRetryAt = nextRetryAt;
        job.LastRunAt = lastRunAt;

        await store.SaveAsync(job);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var row = (await db.BackgroundJobs.ToListAsync()).ShouldHaveSingleItem();
        row.Id.ShouldBe(id);
        row.Status.ShouldBe(BackgroundJobStatus.Pending);
        row.Kind.ShouldBe(JobKind.Recurring);
        row.MaxRetryCount.ShouldBe(5);
        row.NextRetryAt.ShouldBe(nextRetryAt);
        row.LastRunAt.ShouldBe(lastRunAt);
    }
}
