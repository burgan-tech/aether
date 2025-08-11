# Aether SDK - Auditing Entity Usage
This document provides guidance on how to use the auditing entity classes provided by the Aether Framework. These classes are designed to simplify the process of tracking changes to your entities, including who created, modified, or deleted them, and when these actions occurred.

## Overview of Auditing Entities

The Aether Framework provides several base classes for entities that automatically handle auditing concerns. These classes include interfaces and abstract classes for creation, modification, and deletion auditing.

### Key Classes and Interfaces

-   **ICreationAuditedObject**: Interface for entities that track creation information (creation time and user).
-   **IAuditedObject**: Interface for entities that track creation and modification information.
-   **IFullAuditedObject**: Interface for entities that track creation, modification, and deletion information.
-   **CreationAuditedEntity**: Base class implementing `ICreationAuditedObject`.
-   **AuditedEntity**: Base class implementing `IAuditedObject`, inheriting from `CreationAuditedEntity`.
-   **FullAuditedEntity**: Base class implementing `IFullAuditedObject`, inheriting from `AuditedEntity`.
-   **CreationAuditedAggregateRoot**: Base class implementing `ICreationAuditedObject` for aggregate roots.
-   **AuditedAggregateRoot**: Base class implementing `IAuditedObject` for aggregate roots, inheriting from `CreationAuditedAggregateRoot`.
-   **FullAuditedAggregateRoot**: Base class implementing `IFullAuditedObject` for aggregate roots, inheriting from `AuditedAggregateRoot`.

## Usage

### 1. Define Your Entity

First, define your entity class. Choose an appropriate base class depending on the level of auditing you require.

```csharp
using BBT.Aether.Domain.Entities.Auditing;

public class MyEntity : FullAuditedEntity<int>
{
    public string Name { get; set; }
    public string Description { get; set; }
}
```

In this example, `MyEntity` inherits from `FullAuditedEntity<int>`, which provides properties for tracking creation, modification, and deletion information.  The `<int>` specifies that the primary key is of type integer.

### 2.  Populate Auditing Properties

The auditing properties (`CreatedAt`, `CreatedBy`, `ModifiedAt`, `ModifiedBy`, `DeletedAt`, `DeletedBy`, etc.) are typically populated automatically by the application's data access layer or business logic.  You should not directly set these properties in your application code.  Instead, rely on the framework to handle this.

### 3.  Configuration

Ensure that your data access layer (e.g., Entity Framework Core) is configured to automatically populate the auditing properties when entities are created, modified, or deleted. This usually involves intercepting the `SaveChanges` method and updating the auditing properties accordingly.

### Example with Entity Framework Core

Here is an example of how you might configure Entity Framework Core to automatically populate the auditing properties:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using BBT.Aether.Auditing; // Assuming the interfaces are in this namespace
using BBT.Aether.Domain.Entities;

public class MyDbContext : DbContext
{
    // ... existing code ...

    public override int SaveChanges()
    {
        var now = DateTime.UtcNow;
        var username = "CurrentUser"; // Replace with your actual user retrieval mechanism

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is ICreationAuditedObject creationAudited && entry.State == EntityState.Added)
            {
                creationAudited.CreatedAt = now;
                creationAudited.CreatedBy = username;
            }

            if (entry.Entity is IAuditedObject audited && entry.State == EntityState.Modified)
            {
                audited.ModifiedAt = now;
                audited.ModifiedBy = username;
            }

            if (entry.Entity is IFullAuditedObject fullAudited && entry.State == EntityState.Deleted)
            {
                entry.State = EntityState.Modified; // Prevent actual deletion
                fullAudited.IsDeleted = true;
                fullAudited.DeletedAt = now;
                fullAudited.DeletedBy = username;
            }
        }

        return base.SaveChanges();
    }
}
```

### Notes

-   Replace `"CurrentUser"` with the actual mechanism for retrieving the current user's identity.
-   The example above demonstrates a soft delete implementation for `IFullAuditedObject`.  Instead of physically deleting the entity, the `IsDeleted` flag is set to `true`, and the `DeletedAt` and `DeletedBy` properties are populated.
-   Consider using a dedicated auditing library or framework for more advanced auditing scenarios.

## Best Practices

-   **Always use UTC time:** Store all `DateTime` values in UTC to avoid time zone issues.
-   **Centralize auditing logic:** Implement the auditing logic in a central location (e.g., in your data access layer) to ensure consistency.
-   **Consider performance:** Auditing can impact performance, especially in high-volume applications.  Optimize your auditing logic to minimize overhead.
-   **Secure auditing information:** Protect the auditing information from unauthorized access or modification.

By following these guidelines, you can effectively use the Aether Framework's auditing entity classes to track changes to your entities and maintain an audit trail of important events in your application.
