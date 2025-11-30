# Multi-Schema Implementation Notes

## Developer Implementation Guide

### Overview

Multi-schema support in Aether allows applications to dynamically switch between database schemas based on HTTP request context (headers, query strings, or route values). This is particularly useful for multi-tenant applications where each tenant has their own schema.

### Key Design Decisions

1. **Manual Interceptor Registration**: Developers must manually register the database connection interceptor for their chosen provider. This provides flexibility and avoids unnecessary dependencies.

2. **Supported Providers**: PostgreSQL and SQL Server are currently supported through built-in interceptors.

3. **No Automatic Configuration**: Unlike some other Aether features, multi-schema support requires explicit registration of both the resolution middleware and the database interceptor.

## Implementation Steps

### 1. Service Registration

```csharp
// Add schema resolution
builder.Services.AddSchemaResolution(options =>
{
    options.HeaderKey = "X-Schema";
    options.QueryStringKey = "schema";
    options.RouteValueKey = "schema";
    options.ThrowIfNotFound = true;
});

// Add DbContext
builder.Services.AddAetherDbContext<MyDbContext>(options =>
    options.UseNpgsql(connectionString));

// Add appropriate interceptor
// For PostgreSQL:
builder.Services.AddScoped<NpgsqlSchemaConnectionInterceptor>();
builder.Services.AddDbContext<MyDbContext>((sp, options) =>
{
    options.AddInterceptors(sp.GetRequiredService<NpgsqlSchemaConnectionInterceptor>());
});

// For SQL Server:
// builder.Services.AddScoped<SqlServerSchemaConnectionInterceptor>();
// builder.Services.AddDbContext<MyDbContext>((sp, options) =>
// {
//     options.AddInterceptors(sp.GetRequiredService<SqlServerSchemaConnectionInterceptor>());
// });
```

### 2. Middleware Registration

```csharp
var app = builder.Build();

app.UseRouting();                    // 1. Must be first for route-based resolution
app.UseAuthentication();             // 2. If using JWT claims
app.UseSchemaResolution();           // 3. Schema resolution
app.UseUnitOfWorkMiddleware();       // 4. After schema is set
app.MapControllers();                // 5. Endpoints last

app.Run();
```

### 3. Usage in Code

Schema is automatically resolved from HTTP context and available via `ICurrentSchema`:

```csharp
public class MyService
{
    private readonly ICurrentSchema _currentSchema;
    
    public MyService(ICurrentSchema currentSchema)
    {
        _currentSchema = currentSchema;
    }
    
    public void DoWork()
    {
        // Schema is automatically set by middleware
        var schema = _currentSchema.Name;
        // Database queries will use this schema
    }
}
```

## Database Interceptors

### PostgreSQL (NpgsqlSchemaConnectionInterceptor)

- **SQL Command**: `SET search_path = "schema_name"`
- **When**: Executed when database connection opens
- **Package Required**: `Npgsql.EntityFrameworkCore.PostgreSQL`

### SQL Server (SqlServerSchemaConnectionInterceptor)

- **SQL Command**: `SET SCHEMA 'schema_name'`
- **When**: Executed when database connection opens
- **Package Required**: `Microsoft.EntityFrameworkCore.SqlServer`

## Important Notes

### Middleware Order

⚠️ **Critical**: `UseSchemaResolution()` must come **after** `UseRouting()` if you're using route-based schema resolution.

### Interceptor Registration

⚠️ **Important**: You must call `AddDbContext<TDbContext>` a second time to add the interceptor. This is because `AddAetherDbContext` already registers the DbContext, and the interceptor needs to be added separately.

### Schema Validation

Consider implementing schema validation to prevent SQL injection:

```csharp
public class ValidatedSchemaResolutionStrategy : ISchemaResolutionStrategy
{
    private readonly ISchemaResolutionStrategy _innerStrategy;
    private readonly HashSet<string> _allowedSchemas;
    
    public string? TryResolve(HttpContext httpContext)
    {
        var schema = _innerStrategy.TryResolve(httpContext);
        
        if (schema != null && !_allowedSchemas.Contains(schema))
        {
            throw new UnauthorizedAccessException($"Schema '{schema}' is not allowed.");
        }
        
        return schema;
    }
}
```

## Common Issues

1. **Schema not resolving**: Ensure middleware is registered after `UseRouting()`
2. **SQL not executing**: Verify interceptor is registered and added to DbContext
3. **Schema is null**: Check that request contains schema in header/query/route
4. **Wrong schema used**: Verify middleware order in pipeline

## Migration Support

Implement `IMultiSchemaMigrator<TContext>` to apply migrations across all schemas:

```csharp
public class MyMultiSchemaMigrator : IMultiSchemaMigrator<MyDbContext>
{
    public async Task MigrateAllAsync(CancellationToken cancellationToken = default)
    {
        var schemas = new[] { "tenant1", "tenant2", "tenant3" };
        
        foreach (var schema in schemas)
        {
            using (_currentSchema.Use(schema))
            {
                await _dbContext.Database.MigrateAsync(cancellationToken);
            }
        }
    }
}

// Run at startup
await app.UseMultiSchemaMigrationsAsync<MyDbContext>();
```

## Architecture

```
HTTP Request
    ↓
UseRouting()
    ↓
UseSchemaResolution() → ICurrentSchemaResolver → Strategies (Route/Header/Query)
    ↓                           ↓
    |                   ICurrentSchema.Set(schema)
    |                           ↓
    |                   AsyncLocalSchemaAccessor
    ↓
Controller/Service → ICurrentSchema.Name
    ↓
Repository/DbContext
    ↓
NpgsqlSchemaConnectionInterceptor → SET search_path
    ↓
Database (correct schema)
```

## Extension Points

- **Custom Resolution Strategy**: Implement `ISchemaResolutionStrategy`
- **Custom Migrator**: Implement `IMultiSchemaMigrator<TContext>`
- **Schema Provider**: Create custom interceptor inheriting from `DbConnectionInterceptor`


