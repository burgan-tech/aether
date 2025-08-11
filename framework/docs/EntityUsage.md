### Aether SDK - Domain Layer Entity Basics

The Domain layer of the Aether SDK includes the base classes and interfaces for entities that represent your data and business logic. These structures help you model your database tables and business rules.

#### 1. `IEntity` Interface ([src/BBT.Aether.Domain/BBT/Aether/Domain/Entities/IEntity.cs](src/BBT.Aether.Domain/BBT/Aether/Domain/Entities/IEntity.cs))

*   The most basic entity interface.
*   Aims to return the primary keys of the entity with the `GetKeys()` method.
*   The `IEntity<TKey>` interface is used for entities that use a single primary key.

#### 2. `Entity` Class ([src/BBT.Aether.Domain/BBT/Aether/Domain/Entities/Entity.cs](src/BBT.Aether.Domain/BBT/Aether/Domain/Entities/Entity.cs))

*   Implements the `IEntity` interface.
*   The `Entity<TKey>` class is the base class for entities that use a single primary key.
*   The `Id` property represents the unique identifier of the entity.
*   The `GetKeys()` method returns an array containing the `Id` property.

#### 3. `IAggregateRoot` Interface ([src/BBT.Aether.Domain/BBT/Aether/Domain/Entities/IAggregateRoot.cs](src/BBT.Aether.Domain/BBT/Aether/Domain/Entities/IAggregateRoot.cs))

*   Indicates that it is an "aggregate root".
*   The Aggregate Root is responsible for managing the consistency of entities within a cluster.
*   The `IAggregateRoot<TKey>` interface is used for aggregate roots that use a single primary key.

#### 4. `BasicAggregateRoot` Class ([src/BBT.Aether.Domain/BBT/Aether/Domain/Entities/BasicAggregateRoot.cs](src/BBT.Aether.Domain/BBT/Aether/Domain/Entities/BasicAggregateRoot.cs))

*   A basic implementation of the `IAggregateRoot` interface.
*   The `BasicAggregateRoot<TKey>` class is the base class for aggregate roots that use a single primary key.

#### 5. `AggregateRoot` Class ([src/BBT.Aether.Domain/BBT/Aether/Domain/Entities/AggregateRoot.cs](src/BBT.Aether.Domain/BBT/Aether/Domain/Entities/AggregateRoot.cs))

*   Derived from the `BasicAggregateRoot` class and implements the `IHasConcurrencyStamp` interface.
*   The `IHasConcurrencyStamp` interface adds a `ConcurrencyStamp` property for concurrency control.
*   `ConcurrencyStamp` is used to prevent conflicts that may occur during entity updates.
*   The `AggregateRoot<TKey>` class is the base class for aggregate roots that use a single primary key.

#### Additional Information

*   **`IHasConcurrencyStamp` Interface ([src/BBT.Aether.Domain/BBT/Aether/Domain/Entities/IHasConcurrencyStamp.cs](src/BBT.Aether.Domain/BBT/Aether/Domain/Entities/IHasConcurrencyStamp.cs))**: Defines the `ConcurrencyStamp` property used for concurrency control.
*   **`IMultiLingualEntity<TTranslation>` Interface ([src/BBT.Aether.Domain/BBT/Aether/Domain/Entities/IMultiLingualEntity.cs](src/BBT.Aether.Domain/BBT/Aether/Domain/Entities/IMultiLingualEntity.cs))**: Used for entities that support multiple languages.
*   **`IEntityTranslation` Interface ([src/BBT.Aether.Domain/BBT/Aether/Domain/Entities/IEntityTranslation.cs](src/BBT.Aether.Domain/BBT/Aether/Domain/Entities/IEntityTranslation.cs))**: Used for entity translations.

These base classes and interfaces provide you with a starting point when creating your own domain entities using the Aether SDK. The purpose of each class and interface is to help you create a sustainable and testable domain model that suits your application's needs.