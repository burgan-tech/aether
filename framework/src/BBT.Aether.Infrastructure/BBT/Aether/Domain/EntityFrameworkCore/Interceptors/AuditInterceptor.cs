using System;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Auditing;
using BBT.Aether.Clock;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Guids;
using BBT.Aether.Reflection;
using BBT.Aether.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BBT.Aether.Domain.EntityFrameworkCore.Interceptors;

public class AuditInterceptor(
    ICurrentUser currentUser,
    IGuidGenerator guidGenerator,
    IClock clock)
    : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        SetAuditEntity(eventData.Context!);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        SetAuditEntity(eventData.Context!);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public async override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        var response = await base.SavedChangesAsync(eventData, result, cancellationToken);
        return response;
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        var response = base.SavedChanges(eventData, result);
        return response;
    }

    protected virtual void ApplyConceptsForAddedEntity(EntityEntry entry)
    {
        CheckAndSetId(entry);
        SetConcurrencyStampIfNull(entry);
        SetCreationAuditProperties(entry);
        NormalizeDateTimeProperties(entry);
    }

    private void CheckAndSetId(EntityEntry entry)
    {
        if (entry.Entity is IEntity<Guid> entityWithGuidId)
        {
            TrySetGuidId(entry, entityWithGuidId);
        }
    }

    private void TrySetGuidId(EntityEntry entry, IEntity<Guid> entity)
    {
        if (entity.Id != default)
        {
            return;
        }

        var idProperty = entry.Property("Id").Metadata.PropertyInfo!;

        //Check for DatabaseGeneratedAttribute
        var dbGeneratedAttr = ReflectionHelper
            .GetSingleAttributeOrDefault<DatabaseGeneratedAttribute>(
                idProperty
            );

        if (dbGeneratedAttr != null && dbGeneratedAttr.DatabaseGeneratedOption != DatabaseGeneratedOption.None)
        {
            return;
        }

        EntityHelper.TrySetId(
            entity,
            () => guidGenerator.Create(),
            true
        );
    }

    private void SetConcurrencyStampIfNull(EntityEntry entry)
    {
        var entity = entry.Entity as IHasConcurrencyStamp;
        if (entity == null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(entity.ConcurrencyStamp))
        {
            return;
        }

        entity.ConcurrencyStamp = Guid.NewGuid().ToString("N");
    }

    private void UpdateConcurrencyStamp(EntityEntry entry)
    {
        var entity = entry.Entity as IHasConcurrencyStamp;
        if (entity == null)
        {
            return;
        }

        entry.Property("ConcurrencyStamp").OriginalValue = entity.ConcurrencyStamp;
        entity.ConcurrencyStamp = Guid.NewGuid().ToString("N");
    }

    private void SetCreationAuditProperties(EntityEntry entry)
    {
        if (entry.Entity is IHasCreatedAt)
        {
            entry.Property("CreatedAt").CurrentValue = clock.UtcNow;
        }

        if (entry.Entity is ICreationAuditedObject)
        {
            if (!currentUser.UserName.IsNullOrEmpty())
            {
                entry.Property("CreatedBy").CurrentValue = currentUser.UserName;
            }
            else
            {
                entry.Property("CreatedBy").CurrentValue = guidGenerator.Create().ToString("N");
            }

            if (!currentUser.ActorUserName.IsNullOrEmpty())
            {
                entry.Property("CreatedByBehalfOf").CurrentValue = currentUser.ActorUserName;
            }
        }
    }

    private void ApplyConceptsForModifiedEntity(EntityEntry entry)
    {
        if (entry.State == EntityState.Modified && entry.Properties.Any(x =>
                x.IsModified && (x.Metadata.ValueGenerated == ValueGenerated.Never ||
                                 x.Metadata.ValueGenerated == ValueGenerated.OnAdd)))
        {
            IncrementEntityVersionProperty(entry);
            SetModificationAuditProperties(entry);
            NormalizeDateTimeProperties(entry);

            if (entry.Entity is ISoftDelete && entry.Entity.As<ISoftDelete>().IsDeleted)
            {
                SetDeletionAuditProperties(entry);
            }
        }
    }

    private void SetModificationAuditProperties(EntityEntry entry)
    {
        if (entry.Entity is IHasModifyTime)
        {
            entry.Property("ModifiedAt").CurrentValue = clock.UtcNow;
        }

        if (entry.Entity is IModifyAuditedObject)
        {
            if (!currentUser.UserName.IsNullOrEmpty())
            {
                entry.Property("ModifiedBy").CurrentValue = currentUser.UserName;
            }
            else
            {
                //TODO: Warning! Should always come from currentuser
                entry.Property("ModifiedBy").CurrentValue = guidGenerator.Create().ToString("N");
            }

            if (!currentUser.ActorUserName.IsNullOrEmpty())
            {
                entry.Property("ModifiedByBehalfOf").CurrentValue = currentUser.ActorUserName;
            }
        }
    }

    private void SetDeletionAuditProperties(EntityEntry entry)
    {
        if (entry.Entity is IHasDeletionTime)
        {
            entry.Property("DeletedAt").CurrentValue = clock.UtcNow;
        }

        if (entry.Entity is IDeletionAuditedObject)
        {
            if (!currentUser.UserName.IsNullOrEmpty())
            {
                entry.Property("DeletedBy").CurrentValue = currentUser.UserName;
            }
            else
            {
                entry.Property("DeletedBy").CurrentValue = guidGenerator.Create().ToString("N");
            }
        }
    }

    private void IncrementEntityVersionProperty(EntityEntry entry)
    {
        if (entry.Entity is IHasEntityVersion)
        {
            entry.Property("EntityVersion").CurrentValue =
                Convert.ToInt32(entry.Property("EntityVersion").CurrentValue ?? 0) + 1;
        }
    }

    private void NormalizeDateTimeProperties(EntityEntry entry)
    {
        foreach (var property in entry.Properties)
        {
            if (property.Metadata.ClrType == typeof(DateTime) || 
                property.Metadata.ClrType == typeof(DateTime?))
            {
                var currentValue = property.CurrentValue;
                if (currentValue != null && currentValue is DateTime dateTime)
                {
                    property.CurrentValue = clock.NormalizeToUtc(dateTime);
                }
            }
            else if (property.Metadata.ClrType == typeof(DateTimeOffset) || 
                     property.Metadata.ClrType == typeof(DateTimeOffset?))
            {
                var currentValue = property.CurrentValue;
                if (currentValue != null && currentValue is DateTimeOffset dateTimeOffset)
                {
                    property.CurrentValue = clock.NormalizeToUtc(dateTimeOffset);
                }
            }
        }
    }

    private void ApplyConceptsForDeletedEntity(EntityEntry entry)
    {
        if (!(entry.Entity is ISoftDelete))
        {
            return;
        }

        entry.Reload();
        ObjectHelper.TrySetProperty(entry.Entity.As<ISoftDelete>(), x => x.IsDeleted, () => true);
        SetDeletionAuditProperties(entry);
    }

    private void SetAuditEntity(DbContext context)
    {
        foreach (var entry in context!.ChangeTracker.Entries())
        {
            if (entry.State.IsIn(EntityState.Modified, EntityState.Deleted))
            {
                UpdateConcurrencyStamp(entry);
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    ApplyConceptsForAddedEntity(entry);
                    break;
                case EntityState.Modified:
                    ApplyConceptsForModifiedEntity(entry);
                    break;
                case EntityState.Deleted:
                    ApplyConceptsForDeletedEntity(entry);
                    break;
            }
        }
    }
}