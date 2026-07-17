using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.BackgroundJob;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.Domain.Repositories;
using BBT.Aether.Events;
using BBT.Aether.MultiSchema;
using BBT.Aether.Persistence;
using BBT.Aether.Uow;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace BBT.Aether.Postgres.Tests.BackgroundJob;

[Collection("postgres")]
public sealed class FixedBackgroundJobSchemaTests(PostgresFixture fixture)
{
    private readonly string _tenantSchema = "fixed_job_tenant_" + Guid.NewGuid().ToString("N");
    private readonly string _rollbackTenantSchema = "fixed_job_rollback_" + Guid.NewGuid().ToString("N");
    private readonly string _queueSchema = "fixed_job_queue_" + Guid.NewGuid().ToString("N");

    private sealed class BusinessEntity(Guid id, string value) : Entity<Guid>(id)
    {
        public string Value { get; private set; } = value;
    }

    private sealed class BusinessDbContext(DbContextOptions<BusinessDbContext> options)
        : AetherDbContext<BusinessDbContext>(options)
    {
        public DbSet<BusinessEntity> Entities => Set<BusinessEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<BusinessEntity>(entity =>
            {
                entity.ToTable("business_entities");
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Value).IsRequired();
            });
        }
    }

    private sealed class JobDbContext(DbContextOptions<JobDbContext> options)
        : AetherDbContext<JobDbContext>(options), IHasEfCoreBackgroundJobs
    {
        public DbSet<BackgroundJobInfo> BackgroundJobs { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ConfigureBackgroundJob();
        }
    }

    private sealed class TestPayload
    {
        public int Value { get; init; }
    }

    private sealed class NoopJobScheduler : IJobScheduler
    {
        public Task ScheduleAsync(
            string handlerName,
            string jobName,
            string schedule,
            ReadOnlyMemory<byte> payload,
            JobScheduleFailurePolicy? failurePolicyOptions = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ScheduleOneShotAsync(
            string handlerName,
            string jobName,
            DateTime dueAtUtc,
            ReadOnlyMemory<byte> payload,
            JobScheduleFailurePolicy? failurePolicy = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteAsync(
            string handlerName,
            string jobName,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Qualified_names_keeps_job_storage_fixed_and_cross_context_enqueue_atomic()
    {
        await using var root = BuildProvider();
        await ArrangeSchemasAsync(root);

        Guid jobId;
        await using (var scope = root.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var currentSchema = services.GetRequiredService<ICurrentSchema>();
            var uowManager = services.GetRequiredService<IUnitOfWorkManager>();
            var businessProvider = services.GetRequiredService<IAetherDbContextProvider<BusinessDbContext>>();
            var backgroundJobs = services.GetRequiredService<IBackgroundJobService>();

            using (currentSchema.Change(_tenantSchema))
            {
                await using var uow = uowManager.Begin(new UnitOfWorkOptions
                {
                    Scope = UnitOfWorkScopeOption.RequiresNew,
                    IsTransactional = true
                });

                (await businessProvider.GetDbContextAsync()).Entities.Add(
                    new BusinessEntity(Guid.NewGuid(), "tenant-data"));
                currentSchema.Name.ShouldBe(_tenantSchema);

                jobId = await backgroundJobs.EnqueueAsync(
                    "TestHandler",
                    "fixed-schema-job",
                    new TestPayload { Value = 42 },
                    "@daily");
                currentSchema.Name.ShouldBe(_tenantSchema);

                await uow.CommitAsync();
                currentSchema.Name.ShouldBe(_tenantSchema);
            }
        }

        (await CountAsync(_tenantSchema, "business_entities")).ShouldBe(1);
        (await RelationExistsAsync(_queueSchema, "business_entities")).ShouldBeFalse();
        (await CountAsync(_queueSchema, "BackgroundJobs")).ShouldBe(1);
        (await RelationExistsAsync(_tenantSchema, "BackgroundJobs")).ShouldBeFalse();

        await using (var scope = root.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var currentSchema = services.GetRequiredService<ICurrentSchema>();
            var uowManager = services.GetRequiredService<IUnitOfWorkManager>();
            var jobStore = services.GetRequiredService<IJobStore>();
            var leaseStore = services.GetRequiredService<IJobArmingLeaseStore>();
            var serializer = services.GetRequiredService<IEventSerializer>();

            leaseStore.ShouldBeOfType<NpgsqlJobArmingLeaseStore<JobDbContext>>();

            using (currentSchema.Change(_tenantSchema))
            {
                await using var uow = uowManager.Begin(new UnitOfWorkOptions
                {
                    Scope = UnitOfWorkScopeOption.RequiresNew,
                    IsTransactional = true
                });

                var job = await jobStore.GetAsync(jobId);
                job.ShouldNotBeNull();
                job.Status.ShouldBe(BackgroundJobStatus.Pending);
                job.ArmingToken.ShouldBeNull();
                currentSchema.Name.ShouldBe(_tenantSchema);

                var envelope = serializer.Deserialize<CloudEventEnvelope<TestPayload>>(
                    Encoding.UTF8.GetBytes(job.Payload.GetRawText()));
                envelope.ShouldNotBeNull();
                envelope.Schema.ShouldBe(_tenantSchema);
                envelope.Data.Value.ShouldBe(42);

                var claims = await leaseStore.ClaimBatchAsync(
                    10, "fixed-schema-worker", TimeSpan.FromSeconds(30));
                currentSchema.Name.ShouldBe(_tenantSchema);
                claims.Count.ShouldBe(1);
                claims[0].Job.Id.ShouldBe(jobId);

                await uow.CommitAsync();
                currentSchema.Name.ShouldBe(_tenantSchema);
            }
        }

        var queueCountBeforeRollback = await CountAsync(_queueSchema, "BackgroundJobs");

        await using (var scope = root.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var currentSchema = services.GetRequiredService<ICurrentSchema>();
            var uowManager = services.GetRequiredService<IUnitOfWorkManager>();
            var businessProvider = services.GetRequiredService<IAetherDbContextProvider<BusinessDbContext>>();
            var backgroundJobs = services.GetRequiredService<IBackgroundJobService>();

            using (currentSchema.Change(_rollbackTenantSchema))
            {
                await using var uow = uowManager.Begin(new UnitOfWorkOptions
                {
                    Scope = UnitOfWorkScopeOption.RequiresNew,
                    IsTransactional = true
                });

                (await businessProvider.GetDbContextAsync()).Entities.Add(
                    new BusinessEntity(Guid.NewGuid(), "rolled-back-data"));
                await backgroundJobs.EnqueueAsync(
                    "TestHandler",
                    "rolled-back-fixed-schema-job",
                    new TestPayload { Value = 99 },
                    "@daily");
                currentSchema.Name.ShouldBe(_rollbackTenantSchema);

                await uow.SaveChangesAsync();
                currentSchema.Name.ShouldBe(_rollbackTenantSchema);

                await uow.RollbackAsync();
                currentSchema.Name.ShouldBe(_rollbackTenantSchema);
            }
        }

        (await CountAsync(_tenantSchema, "business_entities")).ShouldBe(1);
        (await CountAsync(_rollbackTenantSchema, "business_entities")).ShouldBe(0);
        (await CountAsync(_queueSchema, "BackgroundJobs")).ShouldBe(queueCountBeforeRollback);
        (await RelationExistsAsync(_rollbackTenantSchema, "BackgroundJobs")).ShouldBeFalse();
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddAetherCore(_ => { });
        services.AddAetherNpgsql<BusinessDbContext>(
            fixture.ConnectionString,
            SchemaSwitchingMode.QualifiedNames);
        services.AddAetherNpgsql<JobDbContext>(
            fixture.ConnectionString,
            SchemaSwitchingMode.QualifiedNames);
        services.AddSingleton<IJobScheduler, NoopJobScheduler>();
        services.AddAetherBackgroundJob<JobDbContext>(options => options.Schema = _queueSchema);
        return services.BuildServiceProvider();
    }

    private async Task ArrangeSchemasAsync(IServiceProvider services)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"""
                 CREATE SCHEMA "{_tenantSchema}";
                 CREATE SCHEMA "{_rollbackTenantSchema}";
                 CREATE SCHEMA "{_queueSchema}";
                 CREATE TABLE "{_tenantSchema}".business_entities
                 (
                     "Id" uuid PRIMARY KEY,
                     "Value" text NOT NULL
                 );
                 CREATE TABLE "{_rollbackTenantSchema}".business_entities
                 (
                     "Id" uuid PRIMARY KEY,
                     "Value" text NOT NULL
                 );
                 """;
            await command.ExecuteNonQueryAsync();
        }

        var configurator = services.GetRequiredService<IAetherDbContextConfigurator<JobDbContext>>();
        await using var modelConnection = new NpgsqlConnection(fixture.ConnectionString);
        await modelConnection.OpenAsync();
        await using var context = ActivatorUtilities.CreateInstance<JobDbContext>(
            services,
            configurator.BuildOptions(modelConnection, _queueSchema, new SchemaScopeState()));
        var script = context.Database.GenerateCreateScript()
            .Replace(AetherSchemaModel.Placeholder, _queueSchema, StringComparison.Ordinal);

        await using var ddl = connection.CreateCommand();
        ddl.CommandText = script;
        await ddl.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string schema, string table)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{schema}\".\"{table}\"";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<bool> RelationExistsAsync(string schema, string table)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass(@relation) IS NOT NULL";
        command.Parameters.AddWithValue("relation", $"\"{schema}\".\"{table}\"");
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
