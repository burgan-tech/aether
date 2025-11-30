using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BBT.Aether.Domain.EntityFrameworkCore;

public abstract class AetherDbContext<TDbContext>(
    DbContextOptions<TDbContext> options,
    IDomainEventSink? eventSink = null)
    : DbContext(options)
    where TDbContext : DbContext
{
    private readonly static MethodInfo ConfigureBasePropertiesMethodInfo
        = typeof(AetherDbContext<TDbContext>)
            .GetMethod(
                nameof(ConfigureBaseProperties),
                BindingFlags.Instance | BindingFlags.NonPublic
            )!;

    private readonly static MethodInfo ConfigureValueGeneratedMethodInfo
        = typeof(AetherDbContext<TDbContext>)
            .GetMethod(
                nameof(ConfigureValueGenerated),
                BindingFlags.Instance | BindingFlags.NonPublic
            )!;
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            ConfigureBasePropertiesMethodInfo
                .MakeGenericMethod(entityType.ClrType)
                .Invoke(this, [modelBuilder, entityType]);
            
            ConfigureValueGeneratedMethodInfo
                .MakeGenericMethod(entityType.ClrType)
                .Invoke(this, [modelBuilder, entityType]);
        }
    }
    
     public override int SaveChanges()
    {
        TrackEntityStates();
        var result =  base.SaveChanges();
        PublishDomainEventsToSink();
        return result;
    }

    public async override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = new())
    {
        try
        {
            TrackEntityStates();
            var result =  await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            PublishDomainEventsToSink();
            return result;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            if (ex.Entries.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine(ex.Entries.Count > 1
                    ? "There are some entries which are not saved due to concurrency exception:"
                    : "There is an entry which is not saved due to concurrency exception:");
                foreach (var entry in ex.Entries)
                {
                    sb.AppendLine(entry.ToString());
                }
            }

            throw new AetherDbConcurrencyException(ex.Message, ex);
        }
        finally
        {
            ChangeTracker.AutoDetectChangesEnabled = true;
        }
    }

    public virtual Task<int> SaveChangesOnDbContextAsync(bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        return SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected virtual void ConfigureBaseProperties<TEntity>(ModelBuilder modelBuilder,
        IMutableEntityType mutableEntityType)
        where TEntity : class
    {
        if (mutableEntityType.IsOwned())
        {
            return;
        }

        if (!typeof(IEntity).IsAssignableFrom(typeof(TEntity)))
        {
            return;
        }

        modelBuilder.Entity<TEntity>().ConfigureByConvention();
        ConfigureGlobalFilters<TEntity>(modelBuilder, mutableEntityType);
    }

    protected virtual void ConfigureValueGenerated<TEntity>(ModelBuilder modelBuilder,
        IMutableEntityType mutableEntityType)
        where TEntity : class
    {
        if (!typeof(IEntity<Guid>).IsAssignableFrom(typeof(TEntity)))
        {
            return;
        }

        var idPropertyBuilder = modelBuilder.Entity<TEntity>().Property(x => ((IEntity<Guid>)x).Id);
        if (idPropertyBuilder.Metadata.PropertyInfo!.IsDefined(typeof(DatabaseGeneratedAttribute), true))
        {
            return;
        }

        idPropertyBuilder.ValueGeneratedNever();
    }

    protected virtual void ConfigureGlobalFilters<TEntity>(ModelBuilder modelBuilder,
        IMutableEntityType mutableEntityType)
        where TEntity : class
    {
        if (mutableEntityType.BaseType == null && ShouldFilterEntity<TEntity>(mutableEntityType))
        {
            var filterExpression = CreateFilterExpression<TEntity>();
            if (filterExpression != null)
            {
                modelBuilder.Entity<TEntity>().HasQueryFilter(filterExpression);
            }
        }
    }

    protected virtual bool ShouldFilterEntity<TEntity>(IMutableEntityType entityType) where TEntity : class
    {
        if (typeof(ISoftDelete).IsAssignableFrom(typeof(TEntity)))
        {
            return true;
        }

        return false;
    }

    protected virtual Expression<Func<TEntity, bool>>? CreateFilterExpression<TEntity>()
        where TEntity : class
    {
        Expression<Func<TEntity, bool>>? expression = null;

        if (typeof(ISoftDelete).IsAssignableFrom(typeof(TEntity)))
        {
            expression = e => !EF.Property<bool>(e, "IsDeleted");
        }

        return expression;
    }

    private void TrackEntityStates()
    {
        var addedEntities = ChangeTracker.Entries()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .ToList();

        var modifiedEntities = ChangeTracker.Entries()
            .Where(e => e.State == EntityState.Modified)
            .Select(e => e.Entity)
            .ToList();

        foreach (var entity in addedEntities)
        {
            TrackRelatedEntities(entity, EntityState.Added);
        }

        foreach (var entity in modifiedEntities)
        {
            TrackRelatedEntities(entity, EntityState.Modified);
        }
    }

    private void TrackRelatedEntities(object entity, EntityState state)
    {
        var navigationProperties = entity.GetType().GetProperties()
            .Where(p =>
                typeof(IEnumerable<object>).IsAssignableFrom(p.PropertyType)
                && p.PropertyType != typeof(string[])
                && p.PropertyType != typeof(List<string>)
                && p.PropertyType != typeof(byte[])
                && !typeof(IDictionary<,>).IsAssignableFrom(p.PropertyType)
                && !typeof(Dictionary<,>).IsAssignableFrom(p.PropertyType)
                && !typeof(Tuple<>).IsAssignableFrom(p.PropertyType)
                && !typeof(Tuple<,>).IsAssignableFrom(p.PropertyType)
                && !typeof(Tuple<,,>).IsAssignableFrom(p.PropertyType)
            )
            .ToList();


        foreach (var navigationProperty in navigationProperties)
        {
            var relatedEntities = (IEnumerable<object>?)navigationProperty.GetValue(entity);
            if (relatedEntities != null)
            {
                foreach (var relatedEntity in relatedEntities)
                {
                    if (Entry(relatedEntity).State == EntityState.Detached)
                    {
                        Entry(relatedEntity).State = state;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Collects all domain events from tracked entities that implement IHasDomainEvents.
    /// Public to allow UoW to collect events before commit.
    /// </summary>
    public List<DomainEventEnvelope> CollectDomainEvents()
    {
        var domainEvents = new List<DomainEventEnvelope>();

        var entitiesWithEvents = ChangeTracker.Entries()
            .Where(e => e.Entity is IHasDomainEvents)
            .Select(e => e.Entity as IHasDomainEvents)
            .Where(e => e != null)
            .ToList();

        foreach (var entity in entitiesWithEvents)
        {
            var events = entity!.GetDomainEvents();
            if (events.Any())
            {
                domainEvents.AddRange(events);
            }
        }

        return domainEvents;
    }

    /// <summary>
    /// Clears all domain events from tracked entities that implement IHasDomainEvents.
    /// Public to allow UoW to clear events after successful dispatch.
    /// </summary>
    public void ClearDomainEvents()
    {
        var entitiesWithEvents = ChangeTracker.Entries()
            .Where(e => e.Entity is IHasDomainEvents)
            .Select(e => e.Entity as IHasDomainEvents)
            .Where(e => e != null)
            .ToList();

        foreach (var entity in entitiesWithEvents)
        {
            entity!.ClearDomainEvents();
        }
    }

    /// <summary>
    /// Publishes collected domain events to the sink (Unit of Work).
    /// Called automatically during SaveChanges to push events into the UoW transaction queue.
    /// </summary>
    private void PublishDomainEventsToSink()
    {
        if (eventSink is null)
        {
            // No sink configured - events will be collected explicitly by UoW (backward compatibility)
            return;
        }

        var domainEvents = CollectDomainEvents();
        if (domainEvents.Count == 0)
        {
            return;
        }

        eventSink.EnqueueDomainEvents(domainEvents);
        ClearDomainEvents();
    }
}