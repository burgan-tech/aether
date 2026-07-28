using System;
using System.Threading.Tasks;
using BBT.Aether.Domain.Entities;
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

[Collection("postgres")]
public sealed class FixedMessagingSchemaTests(PostgresFixture fixture)
{
    private readonly string _tenantSchema = "fixed_message_tenant_" + Guid.NewGuid().ToString("N");
    private readonly string _messagingSchema = "fixed_message_queue_" + Guid.NewGuid().ToString("N");

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

    private sealed class MessagingDbContext(DbContextOptions<MessagingDbContext> options)
        : AetherDbContext<MessagingDbContext>(options), IHasEfCoreOutbox, IHasEfCoreInbox
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

    [Fact]
    public async Task Qualified_names_writes_messaging_stores_to_configured_schema_not_current_schema()
    {
        using var root = BuildProvider();
        await ArrangeSchemasAsync(root);
        await using var scope = root.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var currentSchema = services.GetRequiredService<ICurrentSchema>();
        var uowManager = services.GetRequiredService<IUnitOfWorkManager>();
        var businessProvider = services.GetRequiredService<IAetherDbContextProvider<BusinessDbContext>>();
        var outboxStore = services.GetRequiredService<IOutboxStore>();
        var inboxStore = services.GetRequiredService<IInboxStore>();
        var eventId = Guid.NewGuid().ToString("N");

        using (currentSchema.Change(_tenantSchema))
        {
            await using var uow = uowManager.Begin(new UnitOfWorkOptions
            {
                Scope = UnitOfWorkScopeOption.RequiresNew,
                IsTransactional = true
            });

            var businessContext = await businessProvider.GetDbContextAsync();
            businessContext.Entities.Add(new BusinessEntity(Guid.NewGuid(), "tenant-data"));
            var envelope = new CloudEventEnvelope
            {
                Id = eventId,
                Type = "TestEvent",
                Topic = "test-event",
                Data = new { Value = 42 }
            };
            await outboxStore.StoreAsync(envelope);
            await inboxStore.StorePendingAsync(envelope);

            await uow.CommitAsync();
        }

        using (currentSchema.Change(_tenantSchema))
        {
            await using var uow = uowManager.Begin(new UnitOfWorkOptions
            {
                Scope = UnitOfWorkScopeOption.RequiresNew,
                IsTransactional = true
            });

            (await inboxStore.HasProcessedAsync(eventId)).ShouldBeFalse();
            await uow.CommitAsync();
        }

        (await CountAsync(_tenantSchema, "business_entities")).ShouldBe(1);
        (await CountAsync(_messagingSchema, "OutboxMessages")).ShouldBe(1);
        (await CountAsync(_messagingSchema, "InboxMessages")).ShouldBe(1);
        (await RelationExistsAsync(_tenantSchema, "OutboxMessages")).ShouldBeFalse();
        (await RelationExistsAsync(_tenantSchema, "InboxMessages")).ShouldBeFalse();
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddAetherCore(_ => { });
        services.AddAetherNpgsql<BusinessDbContext>(
            fixture.ConnectionString,
            SchemaSwitchingMode.QualifiedNames);
        services.AddAetherNpgsql<MessagingDbContext>(
            fixture.ConnectionString,
            SchemaSwitchingMode.QualifiedNames);
        services.AddSingleton<IEventSerializer, SystemTextJsonEventSerializer>();
        // Task 7 wires this up for real via DI; until then, register the no-op collector so the
        // outbox store's primary constructor (with its configured-schema override behavior) is
        // still what DI selects here, instead of silently falling back to the legacy constructor.
        services.AddScoped<IOutboxSignalCollector, NullOutboxSignalCollector>();
        services.AddAetherOutbox<MessagingDbContext>(options => options.Schema = _messagingSchema);
        services.AddAetherInbox<MessagingDbContext>(options => options.Schema = _messagingSchema);
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
                 CREATE SCHEMA "{_messagingSchema}";
                 CREATE TABLE "{_tenantSchema}".business_entities
                 (
                     "Id" uuid PRIMARY KEY,
                     "Value" text NOT NULL
                 );
                 """;
            await command.ExecuteNonQueryAsync();
        }

        var configurator = services.GetRequiredService<IAetherDbContextConfigurator<MessagingDbContext>>();
        await using var modelConnection = new NpgsqlConnection(fixture.ConnectionString);
        await modelConnection.OpenAsync();
        await using var context = ActivatorUtilities.CreateInstance<MessagingDbContext>(
            services,
            configurator.BuildOptions(modelConnection, _messagingSchema, new SchemaScopeState()));
        var script = context.Database.GenerateCreateScript()
            .Replace("__aether_schema__", _messagingSchema, StringComparison.Ordinal);

        await using var ddl = connection.CreateCommand();
        ddl.CommandText = $"SET search_path TO \"{_messagingSchema}\";\n{script}";
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
