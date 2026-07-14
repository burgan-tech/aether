using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.Domain.Entities;
using BBT.Aether.MultiSchema;
using BBT.Aether.Persistence;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace BBT.Aether.Postgres.Tests;

public sealed class PostgreSqlIdentifierTests
{
    [Theory]
    [InlineData("flow_kyc")]
    [InlineData("runtime_loan")]
    [InlineData("_audit")]
    public void QuoteSchema_allows_valid_identifiers(string schema)
        => PostgreSqlIdentifier.QuoteSchema(schema).ShouldBe($"\"{schema}\"");

    [Theory]
    [InlineData("flow kyc")]
    [InlineData("1flow")]
    [InlineData("flow;drop")]
    [InlineData("")]
    public void QuoteSchema_rejects_invalid_identifiers(string schema)
        => Should.Throw<System.InvalidOperationException>(() => PostgreSqlIdentifier.QuoteSchema(schema));

    [Fact]
    public void QuoteSchema_rejects_identifiers_longer_than_63_bytes()
    {
        var tooLong = new string('a', 64);
        Should.Throw<System.ArgumentException>(() => PostgreSqlIdentifier.QuoteSchema(tooLong));
    }

    [Fact]
    public void QuoteSchema_accepts_identifier_at_the_63_byte_limit()
    {
        var atLimit = new string('a', 63);
        PostgreSqlIdentifier.QuoteSchema(atLimit).ShouldBe($"\"{atLimit}\"");
    }

    [Fact]
    public void QuoteTable_validates_and_quotes_table_identifiers()
    {
        PostgreSqlIdentifier.QuoteTable("OutboxMessages").ShouldBe("\"OutboxMessages\"");
        Should.Throw<System.InvalidOperationException>(() =>
            PostgreSqlIdentifier.QuoteTable("outbox messages"));
    }

    [Fact]
    public void RelationName_uses_runtime_schema_for_placeholder_mapping()
    {
        using var db = CreateRelationDbContext();
        var entityType = db.Model.FindEntityType(typeof(RuntimeEntity))!;

        PostgreSqlRelationName.For(entityType, "tenant")
            .ShouldBe("\"tenant\".\"runtime_items\"");
    }

    [Fact]
    public void RelationName_preserves_explicit_schema_mapping()
    {
        using var db = CreateRelationDbContext();
        var entityType = db.Model.FindEntityType(typeof(ExplicitEntity))!;

        PostgreSqlRelationName.For(entityType, "tenant")
            .ShouldBe("\"audit\".\"audit_items\"");
    }

    [Fact]
    public void RelationName_rejects_entity_without_table_mapping()
    {
        using var db = CreateRelationDbContext();
        var entityType = db.Model.FindEntityType(typeof(ViewEntity))!;

        Should.Throw<System.InvalidOperationException>(() =>
            PostgreSqlRelationName.For(entityType, "tenant"));
    }

    [Fact]
    public void RelationName_qualifies_inbox_and_background_job_mapped_entities()
    {
        using var db = CreateFrameworkRelationDbContext();

        PostgreSqlRelationName.For(
                db.Model.FindEntityType(typeof(BBT.Aether.Domain.Events.InboxMessage))!,
                "tenant")
            .ShouldBe("\"tenant\".\"InboxMessages\"");
        PostgreSqlRelationName.For(
                db.Model.FindEntityType(typeof(BackgroundJobInfo))!,
                "tenant")
            .ShouldBe("\"tenant\".\"BackgroundJobs\"");
    }

    private static RelationDbContext CreateRelationDbContext()
    {
        var options = new DbContextOptionsBuilder<RelationDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        return new RelationDbContext(options);
    }

    private static FrameworkRelationDbContext CreateFrameworkRelationDbContext()
    {
        var options = new DbContextOptionsBuilder<FrameworkRelationDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        return new FrameworkRelationDbContext(options);
    }

    private sealed class RelationDbContext(DbContextOptions<RelationDbContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<RuntimeEntity>().ToTable("runtime_items", AetherSchemaModel.Placeholder);
            modelBuilder.Entity<ExplicitEntity>().ToTable("audit_items", "audit");
            modelBuilder.Entity<ViewEntity>().ToView("items_view");
        }
    }

    private sealed class FrameworkRelationDbContext(DbContextOptions<FrameworkRelationDbContext> options)
        : DbContext(options), IHasEfCoreInbox, IHasEfCoreBackgroundJobs
    {
        public DbSet<BBT.Aether.Domain.Events.InboxMessage> InboxMessages =>
            Set<BBT.Aether.Domain.Events.InboxMessage>();

        public DbSet<BackgroundJobInfo> BackgroundJobs { get; set; } = default!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ConfigureInbox();
            modelBuilder.ConfigureBackgroundJob();
        }
    }

    private sealed class RuntimeEntity
    {
        public int Id { get; set; }
    }

    private sealed class ExplicitEntity
    {
        public int Id { get; set; }
    }

    private sealed class ViewEntity
    {
        public int Id { get; set; }
    }
}
