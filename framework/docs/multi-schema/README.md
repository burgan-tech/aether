# Multi-Schema Support

## Overview

Dynamic schema resolution for multi-tenant applications and data partitioning. Automatically resolves schema from HTTP requests and applies it to database connections via EF Core interceptors.

## Quick Start

### Service Registration

```csharp
// Add schema resolution
builder.Services.AddSchemaResolution(options =>
{
    options.HeaderKey = "X-Schema";      // From header
    options.QueryStringKey = "schema";   // From query string
    options.RouteValueKey = "schema";    // From route
    options.ThrowIfNotFound = true;      // 400 if missing
});

// Add schema interceptor (PostgreSQL)
builder.Services.AddScoped<NpgsqlSchemaConnectionInterceptor>();

// Configure DbContext with interceptor
builder.Services.AddAetherDbContext<MyDbContext>((sp, options) =>
{
    options.UseNpgsql(connectionString);
    options.AddInterceptors(sp.GetRequiredService<NpgsqlSchemaConnectionInterceptor>());
});
```

### Middleware Setup

```csharp
var app = builder.Build();

app.UseRouting();
app.UseSchemaResolution(); // After UseRouting
app.MapControllers();
```

### Usage

```csharp
// Schema resolved automatically from request
[Route("api/{schema}/products")]
public class ProductsController : ControllerBase
{
    // GET api/tenant_a/products → Uses "tenant_a" schema
}

// Or inject ICurrentSchema
public class ProductService
{
    private readonly ICurrentSchema _currentSchema;
    
    public async Task<List<Product>> GetProductsAsync()
    {
        Console.WriteLine($"Current schema: {_currentSchema.Name}");
        return await _repository.GetListAsync();
    }
}
```

## Schema Resolution Strategies

Resolution priority (first match wins):
1. **Route** - `/api/{schema}/products`
2. **Header** - `X-Schema: tenant_a`
3. **Query String** - `?schema=tenant_a`

### Custom Resolution Strategy

```csharp
public class JwtClaimSchemaStrategy : ISchemaResolutionStrategy
{
    public string? TryResolve(HttpContext httpContext)
    {
        var claim = httpContext.User.FindFirst("schema");
        return claim?.Value;
    }
}

// Register
services.AddTransient<ISchemaResolutionStrategy, JwtClaimSchemaStrategy>();
```

## Database Providers

### PostgreSQL

```csharp
services.AddScoped<NpgsqlSchemaConnectionInterceptor>();
// Executes: SET search_path = "schema_name"
```

### SQL Server

```csharp
services.AddScoped<SqlServerSchemaConnectionInterceptor>();
// Executes: SET SCHEMA 'schema_name'
```

## Manual Schema Switching

```csharp
public class ReportService
{
    private readonly ICurrentSchema _currentSchema;
    
    public async Task<Report[]> GetCrossSchemaReportsAsync()
    {
        var reports = new List<Report>();
        
        // Temporarily switch schema
        using (_currentSchema.Use("tenant_a"))
        {
            reports.AddRange(await _repository.GetListAsync());
        }
        
        using (_currentSchema.Use("tenant_b"))
        {
            reports.AddRange(await _repository.GetListAsync());
        }
        
        return reports.ToArray();
    }
}
```

## Migration Support

```csharp
public class MyMultiSchemaMigrator : IMultiSchemaMigrator<MyDbContext>
{
    public async Task MigrateAllAsync(CancellationToken ct = default)
    {
        var schemas = new[] { "tenant_a", "tenant_b", "tenant_c" };
        
        foreach (var schema in schemas)
        {
            using (_currentSchema.Use(schema))
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<MyDbContext>();
                await dbContext.Database.MigrateAsync(ct);
            }
        }
    }
}

// At startup
await app.UseMultiSchemaMigrationsAsync<MyDbContext>();
```

## Configuration

```csharp
services.AddSchemaResolution(options =>
{
    options.HeaderKey = "X-Tenant";        // Header key
    options.QueryStringKey = "tenant";     // Query param key
    options.RouteValueKey = "tenantId";    // Route param key
    options.ThrowIfNotFound = true;        // Return 400 if not resolved
});
```

## Best Practices

1. **Validate schema names** - Prevent SQL injection with allowlist
2. **Place middleware correctly** - After `UseRouting()`, before `UseUnitOfWorkMiddleware()`
3. **Use consistent naming** - Lowercase with underscores: `tenant_a`, `module_orders`
4. **Handle missing schema** - Set `ThrowIfNotFound = true` for APIs requiring schema

## Related Features

- [Repository Pattern](../repository-pattern/README.md) - Data access
- [Unit of Work](../unit-of-work/README.md) - Transaction management
