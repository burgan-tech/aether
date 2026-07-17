using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.MultiSchema;
using BBT.Aether.Persistence;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Aether.BackgroundJob;

public sealed class FixedBackgroundJobSchemaStoreTests
{
    public sealed class JobDbContext(DbContextOptions<JobDbContext> options)
        : DbContext(options), IHasEfCoreBackgroundJobs
    {
        public DbSet<BackgroundJobInfo> BackgroundJobs { get; set; } = default!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ConfigureBackgroundJob();
    }

    [Fact]
    public async Task Job_store_uses_configured_schema_and_restores_tenant()
    {
        await using var db = CreateContext();
        var currentSchema = new StaticCurrentSchema("tenant_a");
        var provider = Substitute.For<IAetherDbContextProvider<JobDbContext>>();
        provider.GetDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            currentSchema.Name.ShouldBe("sys_queues");
            return db;
        });
        var store = new EfCoreJobStore<JobDbContext>(
            provider,
            new BackgroundJobOptions { Schema = "sys_queues" },
            currentSchema);

        (await store.GetAsync(Guid.NewGuid())).ShouldBeNull();
        await store.SaveAsync(CreateJob());

        currentSchema.Name.ShouldBe("tenant_a");
        await provider.Received(2).GetDbContextAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Legacy_constructor_preserves_ambient_schema_behavior()
    {
        await using var db = CreateContext();
        var currentSchema = new StaticCurrentSchema("tenant_a");
        var provider = Substitute.For<IAetherDbContextProvider<JobDbContext>>();
        provider.GetDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            currentSchema.Name.ShouldBe("tenant_a");
            return db;
        });
        var store = new EfCoreJobStore<JobDbContext>(provider);

        (await store.GetAsync(Guid.NewGuid())).ShouldBeNull();

        await provider.Received(1).GetDbContextAsync(Arg.Any<CancellationToken>());
        currentSchema.Name.ShouldBe("tenant_a");
    }

    [Fact]
    public async Task Null_configured_schema_preserves_ambient_schema_behavior()
    {
        await using var db = CreateContext();
        var currentSchema = new StaticCurrentSchema("tenant_a");
        var provider = Substitute.For<IAetherDbContextProvider<JobDbContext>>();
        provider.GetDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            currentSchema.Name.ShouldBe("tenant_a");
            return db;
        });
        var store = new EfCoreJobStore<JobDbContext>(
            provider,
            new BackgroundJobOptions { Schema = null },
            currentSchema);

        (await store.GetAsync(Guid.NewGuid())).ShouldBeNull();

        await provider.Received(1).GetDbContextAsync(Arg.Any<CancellationToken>());
        currentSchema.Name.ShouldBe("tenant_a");
    }

    [Fact]
    public async Task Ef_lease_store_uses_configured_schema_and_restores_tenant()
    {
        await using var db = CreateContext();
        var currentSchema = new StaticCurrentSchema("tenant_a");
        var provider = Substitute.For<IAetherDbContextProvider<JobDbContext>>();
        provider.GetDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            currentSchema.Name.ShouldBe("sys_queues");
            return db;
        });
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTime.UtcNow);
        var store = new EfCoreJobArmingLeaseStore<JobDbContext>(
            provider,
            clock,
            new BackgroundJobOptions { Schema = "sys_queues" },
            currentSchema);

        (await store.ClaimBatchAsync(10, "worker", TimeSpan.FromSeconds(30)))
            .ShouldBeEmpty();

        currentSchema.Name.ShouldBe("tenant_a");
    }

    [Fact]
    public async Task Ef_lease_store_legacy_constructor_preserves_ambient_schema_behavior()
    {
        await using var db = CreateContext();
        var currentSchema = new StaticCurrentSchema("tenant_a");
        var provider = Substitute.For<IAetherDbContextProvider<JobDbContext>>();
        provider.GetDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            currentSchema.Name.ShouldBe("tenant_a");
            return db;
        });
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTime.UtcNow);
        var store = new EfCoreJobArmingLeaseStore<JobDbContext>(provider, clock);

        (await store.ClaimBatchAsync(10, "worker", TimeSpan.FromSeconds(30)))
            .ShouldBeEmpty();

        currentSchema.Name.ShouldBe("tenant_a");
    }

    private static JobDbContext CreateContext() => new(
        new DbContextOptionsBuilder<JobDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static BackgroundJobInfo CreateJob()
        => new(Guid.NewGuid(), "handler", "job");
}
