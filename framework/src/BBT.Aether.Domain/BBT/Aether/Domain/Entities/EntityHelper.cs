using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using BBT.Aether.Domain.Values;
using BBT.Aether.Reflection;

namespace BBT.Aether.Domain.Entities;

public static class EntityHelper
{
    /// <summary>
    /// Checks if two entities are equal.
    /// </summary>
    /// <param name="entity1">First entity.</param>
    /// <param name="entity2">Second entity.</param>
    /// <returns>True if entities are equal, false otherwise.</returns>
    public static bool EntityEquals(IEntity? entity1, IEntity? entity2)
    {
        if (entity1 == null || entity2 == null)
        {
            return false;
        }

        //Same instances must be considered as equal
        if (ReferenceEquals(entity1, entity2))
        {
            return true;
        }

        //Must have a IS-A relation of types or must be same type
        var typeOfEntity1 = entity1.GetType();
        var typeOfEntity2 = entity2.GetType();
        if (!typeOfEntity1.IsAssignableFrom(typeOfEntity2) && !typeOfEntity2.IsAssignableFrom(typeOfEntity1))
        {
            return false;
        }

        //Transient objects are not considered as equal
        if (HasDefaultKeys(entity1) && HasDefaultKeys(entity2))
        {
            return false;
        }

        var entity1Keys = entity1.GetKeys();
        var entity2Keys = entity2.GetKeys();

        if (entity1Keys.Length != entity2Keys.Length)
        {
            return false;
        }

        for (var i = 0; i < entity1Keys.Length; i++)
        {
            var entity1Key = entity1Keys[i];
            var entity2Key = entity2Keys[i];

            if (entity1Key == null)
            {
                if (entity2Key == null)
                {
                    //Both null, so considered as equals
                    continue;
                }

                //entity2Key is not null!
                return false;
            }

            if (entity2Key == null)
            {
                //entity1Key was not null!
                return false;
            }

            if (TypeHelper.IsDefaultValue(entity1Key) && TypeHelper.IsDefaultValue(entity2Key))
            {
                return false;
            }

            if (!entity1Key.Equals(entity2Key))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks if a type is an entity.
    /// </summary>
    /// <param name="type">Type to check.</param>
    /// <returns>True if the type is an entity, false otherwise.</returns>
    public static bool IsEntity(Type type)
    {
        Check.NotNull(type, nameof(type));
        return typeof(IEntity).IsAssignableFrom(type);
    }

    /// <summary>
    /// Predicate to check if a type is a value object.
    /// </summary>
    public static Func<Type, bool> IsValueObjectPredicate = type => typeof(ValueObject).IsAssignableFrom(type);

    /// <summary>
    /// Checks if a type is a value object.
    /// </summary>
    /// <param name="type">Type to check.</param>
    /// <returns>True if the type is a value object, false otherwise.</returns>
    public static bool IsValueObject(Type type)
    {
        Check.NotNull(type, nameof(type));
        return IsValueObjectPredicate(type);
    }

    /// <summary>
    /// Checks if an object is a value object.
    /// </summary>
    /// <param name="obj">Object to check.</param>
    /// <returns>True if the object is a value object, false otherwise.</returns>
    public static bool IsValueObject(object? obj)
    {
        return obj != null && IsValueObject(obj.GetType());
    }

    /// <summary>
    /// Checks if a type is an entity and throws an exception if it is not.
    /// </summary>
    /// <param name="type">Type to check.</param>
    /// <exception cref="AetherException">Thrown if the type is not an entity.</exception>
    public static void CheckEntity(Type type)
    {
        Check.NotNull(type, nameof(type));
        if (!IsEntity(type))
        {
            throw new AetherException($"Given {nameof(type)} is not an entity: {type.AssemblyQualifiedName}. It must implement {typeof(IEntity).AssemblyQualifiedName}.");
        }
    }

    /// <summary>
    /// Checks if a type is an entity with an Id.
    /// </summary>
    /// <param name="type">Type to check.</param>
    /// <returns>True if the type is an entity with an Id, false otherwise.</returns>
    public static bool IsEntityWithId(Type type)
    {
        foreach (var interfaceType in type.GetInterfaces())
        {
            if (interfaceType.GetTypeInfo().IsGenericType &&
                interfaceType.GetGenericTypeDefinition() == typeof(IEntity<>))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if an entity has a default Id.
    /// </summary>
    /// <typeparam name="TKey">Type of the Id.</typeparam>
    /// <param name="entity">Entity to check.</param>
    /// <returns>True if the entity has a default Id, false otherwise.</returns>
    public static bool HasDefaultId<TKey>(IEntity<TKey> entity)
    {
        if (EqualityComparer<TKey>.Default.Equals(entity.Id, default!))
        {
            return true;
        }

        //Workaround for EF Core since it sets int/long to min value when attaching to dbcontext
        if (typeof(TKey) == typeof(int))
        {
            return Convert.ToInt32(entity.Id) <= 0;
        }

        if (typeof(TKey) == typeof(long))
        {
            return Convert.ToInt64(entity.Id) <= 0;
        }

        return false;
    }

    private static bool IsDefaultKeyValue(object? value)
    {
        if (value == null)
        {
            return true;
        }

        var type = value.GetType();

        //Workaround for EF Core since it sets int/long to min value when attaching to DbContext
        if (type == typeof(int))
        {
            return Convert.ToInt32(value) <= 0;
        }

        if (type == typeof(long))
        {
            return Convert.ToInt64(value) <= 0;
        }

        return TypeHelper.IsDefaultValue(value);
    }

    /// <summary>
    /// Checks if an entity has default keys.
    /// </summary>
    /// <param name="entity">Entity to check.</param>
    /// <returns>True if the entity has default keys, false otherwise.</returns>
    public static bool HasDefaultKeys(IEntity entity)
    {
        Check.NotNull(entity, nameof(entity));

        foreach (var key in entity.GetKeys())
        {
            if (!IsDefaultKeyValue(key))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Tries to find the primary key type of the given entity type.
    /// May return null if given type does not implement <see cref="IEntity{TKey}"/>
    /// </summary>
    public static Type? FindPrimaryKeyType<TEntity>()
        where TEntity : IEntity
    {
        return FindPrimaryKeyType(typeof(TEntity));
    }

    /// <summary>
    /// Tries to find the primary key type of the given entity type.
    /// May return null if given type does not implement <see cref="IEntity{TKey}"/>
    /// </summary>
    public static Type? FindPrimaryKeyType(Type entityType)
    {
        if (!typeof(IEntity).IsAssignableFrom(entityType))
        {
            throw new AetherException(
                $"Given {nameof(entityType)} is not an entity. It should implement {typeof(IEntity).AssemblyQualifiedName}!");
        }

        foreach (var interfaceType in entityType.GetTypeInfo().GetInterfaces())
        {
            if (interfaceType.GetTypeInfo().IsGenericType &&
                interfaceType.GetGenericTypeDefinition() == typeof(IEntity<>))
            {
                return interfaceType.GenericTypeArguments[0];
            }
        }

        return null;
    }

    /// <summary>
    /// Creates an equality expression for the Id of an entity.
    /// </summary>
    /// <typeparam name="TEntity">Type of the entity.</typeparam>
    /// <typeparam name="TKey">Type of the Id.</typeparam>
    /// <param name="id">Id to compare.</param>
    /// <returns>An expression that compares the Id of an entity to the given Id.</returns>
    public static Expression<Func<TEntity, bool>> CreateEqualityExpressionForId<TEntity, TKey>(TKey id)
        where TEntity : IEntity<TKey>
    {
        var lambdaParam = Expression.Parameter(typeof(TEntity));
        var leftExpression = Expression.PropertyOrField(lambdaParam, "Id");
        var idValue = Convert.ChangeType(id, typeof(TKey));
        Expression<Func<object?>> closure = () => idValue;
        var rightExpression = Expression.Convert(closure.Body, leftExpression.Type);
        var lambdaBody = Expression.Equal(leftExpression, rightExpression);
        return Expression.Lambda<Func<TEntity, bool>>(lambdaBody, lambdaParam);
    }

    /// <summary>
    /// Tries to set the Id of an entity.
    /// </summary>
    /// <typeparam name="TKey">Type of the Id.</typeparam>
    /// <param name="entity">Entity to set the Id for.</param>
    /// <param name="idFactory">Factory to create the Id.</param>
    /// <param name="checkForDisableIdGenerationAttribute">True if the <see cref="DisableIdGenerationAttribute"/> should be checked.</param>
    public static void TrySetId<TKey>(
        IEntity<TKey> entity,
        Func<TKey> idFactory,
        bool checkForDisableIdGenerationAttribute = false)
    {
        ObjectHelper.TrySetProperty(
            entity,
            x => x.Id,
            idFactory,
            checkForDisableIdGenerationAttribute
                ? new Type[] { typeof(DisableIdGenerationAttribute) }
                : new Type[] { });
    }
}
