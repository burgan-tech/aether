# Domain Repositories Documentation

## Overview

In the Domain layer of our application, we utilize a set of repository interfaces that provide a structured way to interact with our data entities. These repositories abstract the data access logic and allow developers to perform CRUD (Create, Read, Update, Delete) operations in a consistent manner.

## Repository Interfaces

### 1. `IRepository<TEntity>`

The `IRepository<TEntity>` interface extends the `IReadOnlyRepository<TEntity>` and `IBasicRepository<TEntity>` interfaces. It provides methods for retrieving and manipulating entities of type `TEntity`.

#### Key Methods:
- `Task<TEntity?> FindAsync(Expression<Func<TEntity, bool>> predicate, bool includeDetails = true, CancellationToken cancellationToken = default)`: Retrieves a single entity based on a specified condition.
- `Task<TEntity> GetAsync(Expression<Func<TEntity, bool>> predicate, bool includeDetails = true, CancellationToken cancellationToken = default)`: Retrieves a single entity and throws an exception if multiple entities match the condition.
- `Task DeleteAsync(Expression<Func<TEntity, bool>> predicate, bool saveChanges = true, CancellationToken cancellationToken = default)`: Deletes entities that match the specified condition.

### 2. `IBasicRepository<TEntity>`

The `IBasicRepository<TEntity>` interface extends `IReadOnlyBasicRepository<TEntity>`. It provides methods for inserting, updating, and deleting entities.

#### Key Methods:
- `Task<TEntity> InsertAsync(TEntity entity, bool saveChanges = true, CancellationToken cancellationToken = default)`: Inserts a new entity into the repository.
- `Task<TEntity> UpdateAsync(TEntity entity, bool saveChanges = true, CancellationToken cancellationToken = default)`: Updates an existing entity.
- `Task DeleteAsync(TEntity entity, bool saveChanges = true, CancellationToken cancellationToken = default)`: Deletes a specified entity.

### 3. `IReadOnlyRepository<TEntity>`

The `IReadOnlyRepository<TEntity>` interface provides read-only access to entities. It is designed for scenarios where data modification is not required.

#### Key Methods:
- `Task<IQueryable<TEntity>> GetQueryableAsync()`: Returns a queryable collection of entities.
- `Task<List<TEntity>> GetListAsync(Expression<Func<TEntity, bool>> predicate, bool includeDetails = true, CancellationToken cancellationToken = default)`: Retrieves a list of entities based on a specified condition.

### 4. `PaginationParameters`

The `PaginationParameters` class provides parameters for pagination, allowing developers to control the number of results returned and the sorting criteria.

#### Key Properties:
- `string? Sorting`: Specifies the sorting criteria for the results.
- `int SkipCount`: The number of items to skip in the result set.
- `int MaxResultCount`: The maximum number of results to return, with a limit set to prevent excessive data retrieval.

### 5. `PagedList<T>`

The `PagedList<T>` class represents a paged list of items, providing properties to navigate through the pages of results.

#### Key Properties:
- `int CurrentPage`: The current page number.
- `int TotalPages`: The total number of pages available.
- `long TotalCount`: The total number of items in the list.
- `IList<T> Items`: The list of items for the current page.

## Conclusion

The repository interfaces in the Domain layer provide a robust framework for data access, ensuring that developers can efficiently manage entities while adhering to best practices. By utilizing these interfaces, you can maintain a clean separation of concerns and promote code reusability throughout your application.