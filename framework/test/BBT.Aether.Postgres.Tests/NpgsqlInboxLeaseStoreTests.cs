using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.Events;
using BBT.Aether.MultiSchema;
using BBT.Aether.Persistence;
using BBT.Aether.Uow;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;
using InboxMessage = BBT.Aether.Domain.Events.InboxMessage;
using Shouldly;
using Xunit;

namespace BBT.Aether.Postgres.Tests;

[Collection("postgres")]
public sealed class NpgsqlInboxLeaseStoreTests(PostgresFixture fx)
{
    private readonly string _schema = "inbox_lease_test_" + Guid.NewGuid().ToString("N");

    // Minimal IHostEnvironment stub — WorkerIdentity (resolved when the processor builds its
    // workerId) needs one, and NSubstitute is not a dependency of this test project.
    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "inbox-lease-tests";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    // Always-succeeds invoker for "TestEvent" v1 — used to prove that the retry-budget guard
    // stops an exhausted message from ever reaching the processing path. Without the guard, this
    // invoker would happily "process" a crash-looping message (Status -> Processed); with the
    // guard, the message never gets here at all (Status -> DeadLetter, set directly by the guard).
    private sealed class AlwaysSucceedsInvoker : IDistributedEventInvoker
    {
        public string Name => "TestEvent";
        public int Version => 1;
        public string Topic => "test-topic";
        public string PubSubName => "test-pubsub";

        public Task InvokeAsync(IServiceProvider serviceProvider, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class AlwaysSucceedsInvokerRegistry : IDistributedEventInvokerRegistry
    {
        private readonly IDistributedEventInvoker _invoker = new AlwaysSucceedsInvoker();

        public bool TryGet(string name, int version, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IDistributedEventInvoker? invoker)
        {
            invoker = _invoker;
            return true;
        }

        public IEnumerable<IDistributedEventInvoker> All => new[] { _invoker };
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options)
        : AetherDbContext<TestDbContext>(options), IHasEfCoreInbox
    {
        public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ConfigureInbox();
        }
    }

    private IServiceProvider BuildProvider(
        SchemaSwitchingMode mode = SchemaSwitchingMode.TransactionLocal)
    {
        var services = new ServiceCollection();
        services.AddAetherCore(_ => { });
        services.AddAetherNpgsql<TestDbContext>(fx.ConnectionString, mode);
        services.AddAetherInbox<TestDbContext>(options => options.Schema = _schema);
        services.AddSingleton<IEventSerializer, SystemTextJsonEventSerializer>();
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment());
        services.AddSingleton<IDistributedEventInvokerRegistry, AlwaysSucceedsInvokerRegistry>();
        return services.BuildServiceProvider();
    }

    private async Task SetupSchemaAsync(IServiceProvider sp)
    {
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE SCHEMA \"{_schema}\";";
            await cmd.ExecuteNonQueryAsync();
        }

        var configurator = sp.GetRequiredService<
            BBT.Aether.Uow.EntityFrameworkCore.IAetherDbContextConfigurator<TestDbContext>>();
        await using var modelConn = new NpgsqlConnection(fx.ConnectionString);
        await modelConn.OpenAsync();
        await using var ctx = ActivatorUtilities.CreateInstance<TestDbContext>(
            sp, configurator.BuildOptions(modelConn, _schema, new BBT.Aether.Uow.EntityFrameworkCore.SchemaScopeState()));
        var script = ctx.Database.GenerateCreateScript()
            .Replace(AetherSchemaModel.QuotedPlaceholder, $"\"{_schema}\"", StringComparison.Ordinal)
            .Replace(AetherSchemaModel.Placeholder, $"\"{_schema}\"", StringComparison.Ordinal)
            .Replace(
                $"CREATE SCHEMA \"{_schema}\";",
                $"CREATE SCHEMA IF NOT EXISTS \"{_schema}\";",
                StringComparison.Ordinal);

        await using var ddlConn = new NpgsqlConnection(fx.ConnectionString);
        await ddlConn.OpenAsync();
        await using (var setCmd = ddlConn.CreateCommand())
        {
            setCmd.CommandText = $"SET search_path TO \"{_schema}\";";
            await setCmd.ExecuteNonQueryAsync();
        }
        await using (var ddlCmd = ddlConn.CreateCommand())
        {
            ddlCmd.CommandText = script;
            await ddlCmd.ExecuteNonQueryAsync();
        }
    }

    private async Task InsertPendingMessageAsync(IServiceProvider sp)
    {
        await using var scope = sp.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var inboxStore = scope.ServiceProvider.GetRequiredService<IInboxStore>();

        using (currentSchema.Change(_schema))
        {
            await using var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

            await inboxStore.StorePendingAsync(new CloudEventEnvelope
            {
                Id = Guid.NewGuid().ToString(),
                Type = "TestEvent",
                Topic = "test-topic",
                Data = System.Text.Encoding.UTF8.GetBytes("{}")
            });

            await uow.CommitAsync();
        }
    }

    [Fact]
    public async Task LeaseBatch_reclaims_processing_message_with_expired_lease()
    {
        var sp = BuildProvider();
        await SetupSchemaAsync(sp);
        await InsertPendingMessageAsync(sp);

        // Bir worker leaseledi ve çöktü: Status=Processing, LockedUntil geçmişte
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                UPDATE "{_schema}"."InboxMessages"
                SET "Status" = 1,
                    "LockedBy" = 'crashed-worker',
                    "LockedUntil" = now() AT TIME ZONE 'utc' - interval '5 minutes'
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using var scope = sp.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var leaseStore = scope.ServiceProvider.GetRequiredService<IInboxLeaseStore>();

        using (currentSchema.Change(_schema))
        {
            IReadOnlyList<BBT.Aether.Events.InboxMessage> leased;
            await using (var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                leased = await leaseStore.LeaseBatchAsync(10, "worker-2", TimeSpan.FromSeconds(30));
                await uow.CommitAsync();
            }

            leased.Count.ShouldBe(1);
            leased[0].LockedBy.ShouldBe("worker-2");
            // Reclaim edilen satırın RetryCount'u artmalı — crash-loop'ta sonsuz reclaim olmasın
            leased[0].RetryCount.ShouldBe(1);
        }
    }

    [Fact]
    public async Task LeaseBatch_does_not_reclaim_processing_message_with_valid_lease()
    {
        var sp = BuildProvider();
        await SetupSchemaAsync(sp);
        await InsertPendingMessageAsync(sp);

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                UPDATE "{_schema}"."InboxMessages"
                SET "Status" = 1,
                    "LockedBy" = 'healthy-worker',
                    "LockedUntil" = now() AT TIME ZONE 'utc' + interval '5 minutes'
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using var scope = sp.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var leaseStore = scope.ServiceProvider.GetRequiredService<IInboxLeaseStore>();

        using (currentSchema.Change(_schema))
        {
            IReadOnlyList<BBT.Aether.Events.InboxMessage> leased;
            await using (var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                leased = await leaseStore.LeaseBatchAsync(10, "worker-2", TimeSpan.FromSeconds(30));
                await uow.CommitAsync();
            }

            leased.Count.ShouldBe(0);
        }
    }

    [Fact]
    public async Task LeaseBatch_reclaims_processing_message_with_null_lock_expiry()
    {
        var sp = BuildProvider();
        await SetupSchemaAsync(sp);
        await InsertPendingMessageAsync(sp);

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                UPDATE "{_schema}"."InboxMessages"
                SET "Status" = 1,
                    "LockedBy" = 'crashed-worker',
                    "LockedUntil" = NULL
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using var scope = sp.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var leaseStore = scope.ServiceProvider.GetRequiredService<IInboxLeaseStore>();

        using (currentSchema.Change(_schema))
        {
            IReadOnlyList<BBT.Aether.Events.InboxMessage> leased;
            await using (var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                leased = await leaseStore.LeaseBatchAsync(10, "worker-2", TimeSpan.FromSeconds(30));
                await uow.CommitAsync();
            }

            leased.Count.ShouldBe(1);
            leased[0].LockedBy.ShouldBe("worker-2");
            leased[0].RetryCount.ShouldBe(1);
        }
    }

    [Fact]
    public async Task LeaseBatch_still_reclaims_when_retry_count_at_max_so_processor_can_dead_letter()
    {
        var sp = BuildProvider();
        await SetupSchemaAsync(sp);
        await InsertPendingMessageAsync(sp);

        // RetryCount zaten max'ta (varsayılan MaxRetryCount = 5) ve lease süresi dolmuş
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                UPDATE "{_schema}"."InboxMessages"
                SET "Status" = 1,
                    "RetryCount" = 5,
                    "LockedBy" = 'crashed-worker',
                    "LockedUntil" = now() AT TIME ZONE 'utc' - interval '5 minutes'
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using var scope = sp.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var leaseStore = scope.ServiceProvider.GetRequiredService<IInboxLeaseStore>();

        using (currentSchema.Change(_schema))
        {
            IReadOnlyList<BBT.Aether.Events.InboxMessage> leased;
            await using (var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                leased = await leaseStore.LeaseBatchAsync(10, "worker-2", TimeSpan.FromSeconds(30));
                await uow.CommitAsync();
            }

            // Lease store filtrelemiyor — processor dead-letter'a düşürecek
            leased.Count.ShouldBe(1);
            leased[0].RetryCount.ShouldBe(6);
        }
    }

    [Fact]
    public async Task RunAsync_dead_letters_reclaimed_message_whose_retry_budget_is_exhausted()
    {
        var sp = BuildProvider();
        await SetupSchemaAsync(sp);
        await InsertPendingMessageAsync(sp);

        // RetryCount zaten max'ta (varsayılan MaxRetryCount = 5) ve lease süresi dolmuş —
        // worker bu satırı reclaim edecek ama RetryCount 6'ya çıkınca bütçe tükenmiş olacak.
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                UPDATE "{_schema}"."InboxMessages"
                SET "Status" = 1,
                    "RetryCount" = 5,
                    "LockedBy" = 'crashed-worker',
                    "LockedUntil" = now() AT TIME ZONE 'utc' - interval '5 minutes'
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using var scope = sp.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();

        using (currentSchema.Change(_schema))
        {
            var processor = sp.GetRequiredService<IInboxProcessor>();
            await processor.RunAsync();
        }

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT "Status", "LockedBy", "LockedUntil" FROM "{_schema}"."InboxMessages"
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            (await reader.ReadAsync()).ShouldBeTrue();

            reader.GetInt32(reader.GetOrdinal("Status")).ShouldBe((int)IncomingEventStatus.DeadLetter);
            reader.IsDBNull(reader.GetOrdinal("LockedBy")).ShouldBeTrue();
            reader.IsDBNull(reader.GetOrdinal("LockedUntil")).ShouldBeTrue();
        }
    }
}
