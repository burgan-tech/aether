using System;
using System.Linq;
using System.Threading.Tasks;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.TestSupport;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace BBT.Aether.Domain;

public class EfCoreRepositoryUpdateTests
{
    private sealed class Payload
    {
        private Payload()
        {
        }

        public Payload(string json)
        {
            Json = json;
        }

        public string Json { get; private set; } = "{}";
    }

    private sealed class JournalEntry : Entity<Guid>
    {
        private JournalEntry()
        {
        }

        public JournalEntry(Guid id, string name) : base(id)
        {
            Name = name;
            Body = new Payload("{}");
        }

        public string Name { get; private set; } = default!;

        public Payload Body { get; private set; } = default!;

        public void Rename(string name) => Name = name;

        public void SetBody(Payload body) => Body = body;
    }

    private sealed class JournalDbContext(DbContextOptions<JournalDbContext> options)
        : AetherDbContext<JournalDbContext>(options)
    {
        public DbSet<JournalEntry> Entries { get; set; } = default!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<JournalEntry>(b =>
                b.OwnsOne(e => e.Body, d => d.Property(p => p.Json).HasColumnName("Body")));
        }
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static JournalDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<JournalDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    private static EfCoreRepository<JournalDbContext, JournalEntry> NewRepository(JournalDbContext context) =>
        new(new FixedDbContextProvider<JournalDbContext>(context), new NullServiceProvider());

    /// <summary>
    /// A detached entity whose owned value object was REPLACED with a never-tracked instance must
    /// persist the owned columns too. UpdateAsync therefore has to hand the still-detached entity
    /// straight to Update so EF walks the whole graph; attaching first tracks the root as Unchanged,
    /// which makes the follow-up Update touch the root entry only and silently skip the owned
    /// dependents (the InstanceTask Request/Response jsonb regression).
    /// </summary>
    [Fact]
    public async Task UpdateAsync_DetachedEntityWithReplacedOwnedInstance_PersistsOwnedColumns()
    {
        var dbName = $"journal-{Guid.NewGuid():N}";
        var id = Guid.NewGuid();
        var entity = new JournalEntry(id, "created");

        await using (var seed = NewContext(dbName))
        {
            seed.Entries.Add(entity);
            await seed.SaveChangesAsync();
        } // context gone -> entity detached, like after a completed RequiresNew unit of work

        entity.Rename("completed");
        entity.SetBody(new Payload("{\"result\":42}"));

        await using (var update = NewContext(dbName))
        {
            await NewRepository(update).UpdateAsync(entity, saveChanges: true);
        }

        await using var verify = NewContext(dbName);
        var row = await verify.Entries.SingleAsync(e => e.Id == id);
        row.Name.ShouldBe("completed");
        row.Body.Json.ShouldBe("{\"result\":42}");
    }

    /// <summary>
    /// When the same instance is already tracked by the context, UpdateAsync must leave the change
    /// tracker alone and rely on automatic change detection — both scalar and owned changes still land.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_TrackedEntity_StillPersistsScalarAndOwnedChanges()
    {
        var dbName = $"journal-{Guid.NewGuid():N}";
        var id = Guid.NewGuid();

        await using (var context = NewContext(dbName))
        {
            var entity = new JournalEntry(id, "created");
            context.Entries.Add(entity);
            await context.SaveChangesAsync();

            entity.Rename("completed");
            entity.SetBody(new Payload("{\"result\":1}"));

            await NewRepository(context).UpdateAsync(entity, saveChanges: true);
        }

        await using var verify = NewContext(dbName);
        var row = await verify.Entries.SingleAsync(e => e.Id == id);
        row.Name.ShouldBe("completed");
        row.Body.Json.ShouldBe("{\"result\":1}");
    }
}
