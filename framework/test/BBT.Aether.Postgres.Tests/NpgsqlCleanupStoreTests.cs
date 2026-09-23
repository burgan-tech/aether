using System;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
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
using InboxMessage = BBT.Aether.Domain.Events.InboxMessage;
using OutboxMessage = BBT.Aether.Domain.Events.OutboxMessage;

namespace BBT.Aether.Postgres.Tests;

/// <summary>
/// Pins the retention-cleanup contract for several workers sharing one messaging table: the
/// PostgreSQL stores delete disjoint batches via <c>FOR UPDATE SKIP LOCKED</c>, so a worker never
/// waits on another worker's batch and never reports a row another worker deleted as an
/// optimistic-concurrency failure (the "expected to affect 1 row(s), but actually affected 0"
/// error the former read-then-RemoveRange cleanup raised under multiple replicas).
/// </summary>
[Collection("postgres")]
public sealed class NpgsqlCleanupStoreTests(PostgresFixture fx)
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private static readonly TimeSpan NoBlockingBudget = TimeSpan.FromSeconds(10);

    private readonly string _schema = "cleanup_test_" + Guid.NewGuid().ToString("N");

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options)
        : AetherDbContext<TestDbContext>(options), IHasEfCoreOutbox, IHasEfCoreInbox
    {
        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
        public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ConfigureOutbox();
            modelBuilder.ConfigureInbox();
        }
    }

    private ServiceProvider BuildProvider(bool providerFirst = true)
    {
        var services = new ServiceCollection();
        services.AddAetherCore(_ => { });
        if (providerFirst)
            services.AddAetherNpgsql<TestDbContext>(fx.ConnectionString);
        services.AddAetherOutbox<TestDbContext>(options => options.Schema = _schema);
        services.AddAetherInbox<TestDbContext>(options => options.Schema = _schema);
        if (!providerFirst)
            services.AddAetherNpgsql<TestDbContext>(fx.ConnectionString);
        services.AddSingleton<IEventSerializer, SystemTextJsonEventSerializer>();
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Npgsql_cleanup_stores_win_regardless_of_registration_order(bool providerFirst)
    {
        using var sp = BuildProvider(providerFirst);
        using var scope = sp.CreateScope();

        scope.ServiceProvider.GetRequiredService<IInboxCleanupStore>()
            .ShouldBeOfType<NpgsqlInboxCleanupStore<TestDbContext>>();
        scope.ServiceProvider.GetRequiredService<IOutboxCleanupStore>()
            .ShouldBeOfType<NpgsqlOutboxCleanupStore<TestDbContext>>();
    }

    [Fact]
    public async Task Inbox_cleanup_deletes_only_expired_processed_messages_oldest_first()
    {
        await using var sp = BuildProvider();
        await SetupSchemaAsync(sp);
        var now = DateTime.UtcNow;

        await SeedAsync(sp, db =>
        {
            db.InboxMessages.Add(Inbox("old-1", IncomingEventStatus.Processed, now.AddDays(-10)));
            db.InboxMessages.Add(Inbox("old-2", IncomingEventStatus.Processed, now.AddDays(-9)));
            db.InboxMessages.Add(Inbox("old-3", IncomingEventStatus.Processed, now.AddDays(-8)));
            db.InboxMessages.Add(Inbox("fresh", IncomingEventStatus.Processed, now.AddDays(-1)));
            db.InboxMessages.Add(Inbox("pending", IncomingEventStatus.Pending, null));
            db.InboxMessages.Add(Inbox("dead", IncomingEventStatus.DeadLetter, now.AddDays(-30)));
        });

        (await InboxCleanupAsync(sp, batchSize: 2)).ShouldBe(2);
        (await RemainingInboxIdsAsync()).ShouldBe(["dead", "fresh", "old-3", "pending"]);

        (await InboxCleanupAsync(sp, batchSize: 100)).ShouldBe(1);
        (await InboxCleanupAsync(sp, batchSize: 100)).ShouldBe(0);
        (await RemainingInboxIdsAsync()).ShouldBe(["dead", "fresh", "pending"]);
    }

    [Fact]
    public async Task Concurrent_inbox_cleanups_skip_each_others_rows_without_blocking_or_failing()
    {
        await using var sp = BuildProvider();
        await SetupSchemaAsync(sp);
        var now = DateTime.UtcNow;

        await SeedAsync(sp, db =>
        {
            for (var i = 0; i < 10; i++)
                db.InboxMessages.Add(Inbox($"old-{i:D2}", IncomingEventStatus.Processed, now.AddDays(-10).AddMinutes(i)));
        });

        // Worker A deletes the 4 oldest rows and keeps its transaction (and row locks) open.
        await using var scopeA = sp.CreateAsyncScope();
        var schemaA = scopeA.ServiceProvider.GetRequiredService<ICurrentSchema>();
        using var schemaScopeA = schemaA.Change(_schema);
        await using var uowA = scopeA.ServiceProvider.GetRequiredService<IUnitOfWorkManager>().BeginRequiresNew();
        var deletedByA = await scopeA.ServiceProvider.GetRequiredService<IInboxCleanupStore>()
            .DeleteProcessedAsync(4, Retention);
        deletedByA.ShouldBe(4);

        // Worker B runs while A is still open: it must neither wait for A's locks nor fail.
        var workerB = InboxCleanupAsync(sp, batchSize: 100);
        (await Task.WhenAny(workerB, Task.Delay(NoBlockingBudget))).ShouldBe(workerB);
        (await workerB).ShouldBe(6);

        await uowA.CommitAsync();

        (await RemainingInboxIdsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Outbox_cleanup_deletes_only_expired_processed_messages_oldest_first()
    {
        await using var sp = BuildProvider();
        await SetupSchemaAsync(sp);
        var now = DateTime.UtcNow;
        var old1 = Guid.NewGuid();
        var old2 = Guid.NewGuid();
        var fresh = Guid.NewGuid();
        var pending = Guid.NewGuid();

        await SeedAsync(sp, db =>
        {
            db.OutboxMessages.Add(Outbox(old1, OutboxMessageStatus.Processed, now.AddDays(-10)));
            db.OutboxMessages.Add(Outbox(old2, OutboxMessageStatus.Processed, now.AddDays(-9)));
            db.OutboxMessages.Add(Outbox(fresh, OutboxMessageStatus.Processed, now.AddDays(-1)));
            db.OutboxMessages.Add(Outbox(pending, OutboxMessageStatus.Pending, null));
        });

        (await OutboxCleanupAsync(sp, batchSize: 1)).ShouldBe(1);
        (await RemainingOutboxIdsAsync()).ShouldBe(new[] { old2, fresh, pending }, ignoreOrder: true);

        (await OutboxCleanupAsync(sp, batchSize: 100)).ShouldBe(1);
        (await RemainingOutboxIdsAsync()).ShouldBe(new[] { fresh, pending }, ignoreOrder: true);
    }

    [Fact]
    public async Task Concurrent_outbox_cleanups_skip_each_others_rows_without_blocking_or_failing()
    {
        await using var sp = BuildProvider();
        await SetupSchemaAsync(sp);
        var now = DateTime.UtcNow;

        await SeedAsync(sp, db =>
        {
            for (var i = 0; i < 10; i++)
                db.OutboxMessages.Add(Outbox(Guid.NewGuid(), OutboxMessageStatus.Processed, now.AddDays(-10).AddMinutes(i)));
        });

        await using var scopeA = sp.CreateAsyncScope();
        var schemaA = scopeA.ServiceProvider.GetRequiredService<ICurrentSchema>();
        using var schemaScopeA = schemaA.Change(_schema);
        await using var uowA = scopeA.ServiceProvider.GetRequiredService<IUnitOfWorkManager>().BeginRequiresNew();
        (await scopeA.ServiceProvider.GetRequiredService<IOutboxCleanupStore>()
            .DeleteProcessedAsync(4, Retention)).ShouldBe(4);

        var workerB = OutboxCleanupAsync(sp, batchSize: 100);
        (await Task.WhenAny(workerB, Task.Delay(NoBlockingBudget))).ShouldBe(workerB);
        (await workerB).ShouldBe(6);

        await uowA.CommitAsync();

        (await RemainingOutboxIdsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Ef_core_fallback_inbox_cleanup_reports_zero_for_rows_already_deleted_instead_of_throwing()
    {
        await using var sp = BuildProvider();
        await SetupSchemaAsync(sp);
        var now = DateTime.UtcNow;
        await SeedAsync(sp, db =>
            db.InboxMessages.Add(Inbox("old", IncomingEventStatus.Processed, now.AddDays(-10))));

        (await InboxCleanupAsync(sp, batchSize: 10)).ShouldBe(1);

        await using var scope = sp.CreateAsyncScope();
        var services = scope.ServiceProvider;
        using (services.GetRequiredService<ICurrentSchema>().Change(_schema))
        {
            var fallback = ActivatorUtilities.CreateInstance<EfCoreInboxCleanupStore<TestDbContext>>(services);
            await using var uow = services.GetRequiredService<IUnitOfWorkManager>().BeginRequiresNew();
            (await fallback.DeleteProcessedAsync(10, Retention)).ShouldBe(0);
            await uow.CommitAsync();
        }
    }

    private static InboxMessage Inbox(string id, IncomingEventStatus status, DateTime? handledTime) =>
        new(id, "TestEvent", "{}"u8.ToArray())
        {
            CreatedAt = (handledTime ?? DateTime.UtcNow).AddMinutes(-1),
            Status = status,
            HandledTime = handledTime,
        };

    private static OutboxMessage Outbox(Guid id, OutboxMessageStatus status, DateTime? processedAt) =>
        new(id, "TestEvent", "{}"u8.ToArray())
        {
            CreatedAt = (processedAt ?? DateTime.UtcNow).AddMinutes(-1),
            Status = status,
            ProcessedAt = processedAt,
        };

    private async Task SeedAsync(IServiceProvider sp, Action<TestDbContext> seed)
    {
        await using var scope = sp.CreateAsyncScope();
        var services = scope.ServiceProvider;
        using (services.GetRequiredService<ICurrentSchema>().Change(_schema))
        {
            await using var uow = services.GetRequiredService<IUnitOfWorkManager>().BeginRequiresNew();
            var db = await services.GetRequiredService<IAetherDbContextProvider<TestDbContext>>().GetDbContextAsync();
            seed(db);
            await uow.CommitAsync();
        }
    }

    private Task<int> InboxCleanupAsync(IServiceProvider sp, int batchSize) =>
        CleanupAsync(sp, s => s.GetRequiredService<IInboxCleanupStore>().DeleteProcessedAsync(batchSize, Retention));

    private Task<int> OutboxCleanupAsync(IServiceProvider sp, int batchSize) =>
        CleanupAsync(sp, s => s.GetRequiredService<IOutboxCleanupStore>().DeleteProcessedAsync(batchSize, Retention));

    private async Task<int> CleanupAsync(IServiceProvider sp, Func<IServiceProvider, Task<int>> delete)
    {
        await using var scope = sp.CreateAsyncScope();
        var services = scope.ServiceProvider;
        using (services.GetRequiredService<ICurrentSchema>().Change(_schema))
        {
            await using var uow = services.GetRequiredService<IUnitOfWorkManager>().BeginRequiresNew();
            var deleted = await delete(services);
            await uow.CommitAsync();
            return deleted;
        }
    }

    private async Task<string[]> RemainingInboxIdsAsync()
    {
        var ids = new System.Collections.Generic.List<string>();
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT \"Id\" FROM \"{_schema}\".\"InboxMessages\" ORDER BY \"Id\"";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetString(0));
        return ids.ToArray();
    }

    private async Task<Guid[]> RemainingOutboxIdsAsync()
    {
        var ids = new System.Collections.Generic.List<Guid>();
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT \"Id\" FROM \"{_schema}\".\"OutboxMessages\"";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetGuid(0));
        return ids.ToArray();
    }

    private async Task SetupSchemaAsync(IServiceProvider sp)
    {
        var configurator = sp.GetRequiredService<IAetherDbContextConfigurator<TestDbContext>>();
        await using var modelConn = new NpgsqlConnection(fx.ConnectionString);
        await modelConn.OpenAsync();
        await using var ctx = ActivatorUtilities.CreateInstance<TestDbContext>(
            sp, configurator.BuildOptions(modelConn, _schema, new SchemaScopeState()));
        var script = ctx.Database.GenerateCreateScript()
            .Replace(AetherSchemaModel.QuotedPlaceholder, $"\"{_schema}\"", StringComparison.Ordinal)
            .Replace(AetherSchemaModel.Placeholder, $"\"{_schema}\"", StringComparison.Ordinal);

        await using var ddlConn = new NpgsqlConnection(fx.ConnectionString);
        await ddlConn.OpenAsync();
        await using var ddlCmd = ddlConn.CreateCommand();
        ddlCmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{_schema}\"; SET search_path TO \"{_schema}\";\n"
            + script.Replace($"CREATE SCHEMA \"{_schema}\";", string.Empty, StringComparison.Ordinal);
        await ddlCmd.ExecuteNonQueryAsync();
    }
}
