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
using BBT.Aether.Domain.Events;
using BBT.Aether.Domain.Events.Distributed;
using BBT.Aether.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BBT.Aether.Domain.EntityFrameworkCore;

public abstract class AetherDbContext<TDbContext>(
    DbContextOptions<TDbContext> options,
    IEventContext? eventContext = null,
    ITransactionService? transactionService = null
)
    : DbContext(options)
    where TDbContext : DbContext
{
    private readonly IEventContext? _eventContext = eventContext;
    private readonly ITransactionService? _transactionService = transactionService;
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
                .Invoke(this, new object[] { modelBuilder, entityType });
            
            ConfigureValueGeneratedMethodInfo
                .MakeGenericMethod(entityType.ClrType)
                .Invoke(this, new object[] { modelBuilder, entityType });
        }
    }
    
     public override int SaveChanges()
    {
        TrackEntityStates();
        
        if (_eventContext != null)
        {
            return SaveChangesWithDomainEvents().GetAwaiter().GetResult();
        }
        
        return base.SaveChanges();
    }

    public async override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = new())
    {
        try
        {
            TrackEntityStates();
            
            if (_eventContext != null)
            {
                return await SaveChangesWithDomainEvents(acceptAllChangesOnSuccess, cancellationToken);
            }
            
            var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
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
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private async Task<int> SaveChangesWithDomainEvents(bool acceptAllChangesOnSuccess = true, CancellationToken cancellationToken = default)
    {
        // Check if we're in a transaction managed by ITransactionService
        if (_transactionService?.HasActiveTransaction == true)
        {
            return await SaveChangesWithTransactionAwareEvents(acceptAllChangesOnSuccess, cancellationToken);
        }

        // Normal flow - no active transaction service
        return await SaveChangesWithImmediateEvents(acceptAllChangesOnSuccess, cancellationToken);
    }

    private async Task<int> SaveChangesWithTransactionAwareEvents(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken)
    {
        // 1) Collect events from tracked aggregate roots
        var aggregates = ChangeTracker
            .Entries()
            .Where(e => e.Entity is IAggregateRoot)
            .Select(e => (IAggregateRoot)e.Entity)
            .ToList();

        // 2) Collect events by type
        var preCommitEvents = aggregates
            .SelectMany(a => GetDomainEvents(a))
            .OfType<IPreCommitEvent>()
            .ToList();

        var postCommitEvents = aggregates
            .SelectMany(a => GetDomainEvents(a))
            .OfType<IPostCommitEvent>()
            .ToList();

        var distributedEvents = aggregates
            .SelectMany(a => GetDistributedEvents(a))
            .ToList();

        // 3) Dispatch pre-commit events (within transaction)
        if (preCommitEvents.Count > 0 && _eventContext != null)
        {
            await _eventContext.DispatchPreCommitEventsAsync(preCommitEvents, cancellationToken);
        }

        // 4) Save changes to database
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        // 5) Store post-commit and distributed events for later dispatch
        if (_transactionService is ITransactionEventStorage eventStorage)
        {
            eventStorage.StorePostCommitEvents(postCommitEvents);
            eventStorage.StoreDistributedEvents(distributedEvents);
        }

        // 6) Clear events from aggregates
        foreach (var aggregate in aggregates)
        {
            ClearDomainEvents(aggregate);
            ClearDistributedEvents(aggregate);
        }

        return result;
    }

    private async Task<int> SaveChangesWithImmediateEvents(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken)
    {
        // 1) Collect events from tracked aggregate roots
        var aggregates = ChangeTracker
            .Entries()
            .Where(e => e.Entity is IAggregateRoot)
            .Select(e => (IAggregateRoot)e.Entity)
            .ToList();

        // 2) Collect local events
        var preCommitEvents = aggregates
            .SelectMany(a => GetDomainEvents(a))
            .OfType<IPreCommitEvent>()
            .ToList();

        var postCommitEvents = aggregates
            .SelectMany(a => GetDomainEvents(a))
            .OfType<IPostCommitEvent>()
            .ToList();

        // 3) Collect distributed events
        var distributedEvents = aggregates
            .SelectMany(a => GetDistributedEvents(a))
            .ToList();

        // 4) Dispatch pre-commit events (within transaction)
        if (preCommitEvents.Count > 0 && _eventContext != null)
        {
            await _eventContext.DispatchPreCommitEventsAsync(preCommitEvents, cancellationToken);
        }

        // 5) Commit changes to database
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        try
        {
            // 6) Dispatch post-commit events (after successful save)
            if (postCommitEvents.Count > 0 && _eventContext != null)
            {
                await _eventContext.DispatchPostCommitEventsAsync(postCommitEvents, cancellationToken);
            }

            // 7) Publish distributed events (after local events)
            if (distributedEvents.Count > 0 && _eventContext != null)
            {
                await _eventContext.PublishDistributedEventsAsync(distributedEvents, cancellationToken);
            }
        }
        finally
        {
            // 8) Clear all events from aggregates (important for idempotency)
            foreach (var aggregate in aggregates)
            {
                ClearDomainEvents(aggregate);
                ClearDistributedEvents(aggregate);
            }
        }

        return result;
    }

    private static IEnumerable<IDomainEvent> GetDomainEvents(IAggregateRoot aggregate)
    {
        // Use reflection to get DomainEvents property from BasicAggregateRoot
        var domainEventsProperty = aggregate.GetType()
            .GetProperty("DomainEvents", BindingFlags.Public | BindingFlags.Instance);
        
        if (domainEventsProperty?.GetValue(aggregate) is IEnumerable<IDomainEvent> events)
        {
            return events;
        }

        return Enumerable.Empty<IDomainEvent>();
    }

    private static void ClearDomainEvents(IAggregateRoot aggregate)
    {
        // Use reflection to call ClearDomainEvents method from BasicAggregateRoot
        var clearMethod = aggregate.GetType()
            .GetMethod("ClearDomainEvents", BindingFlags.Public | BindingFlags.Instance);
        
        clearMethod?.Invoke(aggregate, null);
    }

    private static IEnumerable<IDistributedDomainEvent> GetDistributedEvents(IAggregateRoot aggregate)
    {
        // Use reflection to get DistributedEvents property from BasicAggregateRoot
        var distributedEventsProperty = aggregate.GetType()
            .GetProperty("DistributedEvents", BindingFlags.Public | BindingFlags.Instance);
        
        if (distributedEventsProperty?.GetValue(aggregate) is IEnumerable<IDistributedDomainEvent> events)
        {
            return events;
        }

        return Enumerable.Empty<IDistributedDomainEvent>();
    }

    private static void ClearDistributedEvents(IAggregateRoot aggregate)
    {
        // Use reflection to call ClearDistributedEvents method from BasicAggregateRoot
        var clearMethod = aggregate.GetType()
            .GetMethod("ClearDistributedEvents", BindingFlags.Public | BindingFlags.Instance);
        
        clearMethod?.Invoke(aggregate, null);
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
}