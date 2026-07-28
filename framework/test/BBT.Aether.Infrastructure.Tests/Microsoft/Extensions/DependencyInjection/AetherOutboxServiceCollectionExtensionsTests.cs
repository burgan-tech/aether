using System.Linq;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.Events;
using BBT.Aether.Events.Processing;
using BBT.Aether.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using InboxMessage = BBT.Aether.Domain.Events.InboxMessage;
using OutboxMessage = BBT.Aether.Domain.Events.OutboxMessage;

namespace Microsoft.Extensions.DependencyInjection;

public sealed class AetherOutboxServiceCollectionExtensionsTests
{
    private sealed class TestDbContext(DbContextOptions<TestDbContext> options)
        : DbContext(options), IHasEfCoreOutbox, IHasEfCoreInbox
    {
        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
        public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ConfigureOutbox();
            modelBuilder.ConfigureInbox();
        }
    }

    [Fact]
    public void AddAetherOutbox_registers_signalling_services_with_the_intended_lifetimes()
    {
        var services = new ServiceCollection();
        services.AddAetherCore(_ => { });
        services.AddAetherOutbox<TestDbContext>(options => options.Schema = "s");

        Descriptor(services, typeof(IOutboxSignalCollector)).Lifetime.ShouldBe(ServiceLifetime.Scoped);
        Descriptor(services, typeof(IOutboxWakeupPublisher)).Lifetime.ShouldBe(ServiceLifetime.Singleton);
        Descriptor(services, typeof(IOutboxSignalCoordinator)).Lifetime.ShouldBe(ServiceLifetime.Singleton);

        static ServiceDescriptor Descriptor(IServiceCollection s, System.Type t) =>
            s.Last(d => d.ServiceType == t);
    }

    [Fact]
    public void AddAetherOutboxDaprSignalling_replaces_the_null_publisher_when_called_after()
    {
        var services = new ServiceCollection();
        services.AddAetherCore(_ => { });
        services.AddAetherOutbox<TestDbContext>(options => options.Schema = "s");
        services.AddAetherOutboxDaprSignalling();

        var descriptor = services.Last(d => d.ServiceType == typeof(IOutboxWakeupPublisher));
        descriptor.Lifetime.ShouldBe(ServiceLifetime.Singleton);
        descriptor.ImplementationType.ShouldBe(typeof(DaprOutboxWakeupPublisher));
        services.Count(d => d.ServiceType == typeof(IOutboxWakeupPublisher)).ShouldBe(1);
    }

    [Fact]
    public void AddAetherOutboxDaprSignalling_wins_when_called_before()
    {
        var services = new ServiceCollection();
        services.AddAetherCore(_ => { });
        services.AddAetherOutboxDaprSignalling();
        services.AddAetherOutbox<TestDbContext>(options => options.Schema = "s");

        var descriptor = services.Last(d => d.ServiceType == typeof(IOutboxWakeupPublisher));
        descriptor.Lifetime.ShouldBe(ServiceLifetime.Singleton);
        descriptor.ImplementationType.ShouldBe(typeof(DaprOutboxWakeupPublisher));
    }
}
