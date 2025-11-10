# Aether PostSharp Aspects Usage Guide

## Overview

This guide covers the PostSharp-based aspect-oriented programming features in the Aether SDK, focusing on the Unit of Work aspect implementation.

## Table of Contents

1. [Installation](#installation)
2. [ASP.NET Core Setup](#aspnet-core-setup)
3. [Using UnitOfWorkAttribute](#using-unitofworkattribute)
4. [Extending UnitOfWorkAttribute](#extending-unitofworkattribute)
5. [Helper Extension Methods](#helper-extension-methods)
6. [UnitOfWorkMiddleware](#unitofworkmiddleware)
7. [Console/Worker Applications](#consoleworker-applications)
8. [Custom Aspects](#custom-aspects)

## Installation

Add the following NuGet packages to your project:

```xml
<PackageReference Include="BBT.Aether.Aspects" Version="..." />
<PackageReference Include="BBT.Aether.AspNetCore" Version="..." />
```

## ASP.NET Core Setup

### Basic Setup

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

// Register Aether services
builder.Services.AddAetherDbContext<MyDbContext>(options => 
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddAetherUnitOfWork<MyDbContext>();
builder.Services.AddAetherDomainEvents<MyDbContext>();

// Register ambient service provider for aspects
builder.Services.AddAetherAmbientServiceProvider();

// Optional: Register UnitOfWorkMiddleware
builder.Services.AddAetherUnitOfWorkMiddleware(options =>
{
    // Customize which requests start UoW
    options.ExcludedHttpMethods.Add("GET");
    options.ExcludedPaths.Add("/health");
    options.IsTransactional = true;
    options.IsolationLevel = IsolationLevel.ReadCommitted;
});

var app = builder.Build();

// Use ambient service provider middleware (required for aspects)
app.UseAetherAmbientServiceProvider();

// Optional: Use UnitOfWorkMiddleware to automatically manage UoW per request
app.UseAetherUnitOfWork();

app.MapControllers();
app.Run();
```

## Using UnitOfWorkAttribute

### Basic Usage

Apply `[UnitOfWork]` attribute to methods that need automatic transaction management:

```csharp
public class ProductService
{
    private readonly IRepository<Product> _productRepo;
    private readonly IRepository<Category> _categoryRepo;

    // Automatic UoW with default options
    [UnitOfWork]
    public virtual async Task UpdateProductAsync(Guid id, decimal newPrice, CancellationToken ct)
    {
        var product = await _productRepo.GetAsync(id);
        product.UpdatePrice(newPrice);
        await _productRepo.UpdateAsync(product, saveChanges: false);
        // Auto-commits on success, auto-rollbacks on exception
    }

    // With custom options
    [UnitOfWork(IsTransactional = true, Scope = UnitOfWorkScopeOption.RequiresNew)]
    public virtual async Task CreateProductAsync(ProductDto dto, CancellationToken ct)
    {
        var category = await _categoryRepo.GetAsync(dto.CategoryId);
        var product = new Product(dto.Name, dto.Price, category);
        await _productRepo.InsertAsync(product, saveChanges: false);
    }

    // Suppress transactions
    [UnitOfWork(Scope = UnitOfWorkScopeOption.Suppress)]
    public virtual async Task<ProductDto> GetProductAsync(Guid id, CancellationToken ct)
    {
        var product = await _productRepo.GetAsync(id);
        return MapToDto(product);
    }
}
```

### Attribute Properties

```csharp
[UnitOfWork(
    IsTransactional = true,                          // Default: true
    Scope = UnitOfWorkScopeOption.Required,          // Default: Required
    IsolationLevel = IsolationLevel.ReadCommitted    // Default: ReadCommitted
)]
```

**Scope Options:**
- `Required`: Participates in existing UoW or creates new one (default)
- `RequiresNew`: Always creates a new UoW, suspending existing one
- `Suppress`: Disables UoW for this method

## Extending UnitOfWorkAttribute

Create custom aspects by extending `UnitOfWorkAttribute`:

```csharp
public class AuditedUnitOfWorkAttribute : UnitOfWorkAttribute
{
    protected override async Task OnBeforeAsync(MethodInterceptionArgs args)
    {
        var logger = GetServiceProvider().GetService<ILogger>();
        var methodName = args.Method.Name;
        var userName = GetServiceProvider()
            .GetService<ICurrentUser>()?.UserName ?? "Anonymous";
        
        logger?.LogInformation(
            "Starting UoW for {Method} by user {User}", 
            methodName, userName);
    }

    protected override async Task OnAfterAsync(MethodInterceptionArgs args)
    {
        var logger = GetServiceProvider().GetService<ILogger>();
        logger?.LogInformation("Successfully completed UoW for {Method}", args.Method.Name);
    }

    protected override async Task OnExceptionAsync(MethodInterceptionArgs args, Exception ex)
    {
        var logger = GetServiceProvider().GetService<ILogger>();
        logger?.LogError(ex, "UoW failed for {Method}", args.Method.Name);
    }
}

// Usage
public class OrderService
{
    [AuditedUnitOfWork]
    public virtual async Task PlaceOrderAsync(OrderDto dto, CancellationToken ct)
    {
        // Your logic here
    }
}
```

## Helper Extension Methods

For scenarios where you can't use attributes (e.g., lambdas, dynamic methods):

```csharp
public class PaymentService
{
    private readonly IUnitOfWorkManager _uowManager;
    private readonly IRepository<Payment> _paymentRepo;

    public async Task ProcessPaymentAsync(PaymentDto dto, CancellationToken ct)
    {
        // Execute with automatic UoW management
        await _uowManager.ExecuteInUowAsync(async _ =>
        {
            var payment = new Payment(dto.Amount, dto.Currency);
            await _paymentRepo.InsertAsync(payment, saveChanges: false);
            // Auto-commits
        }, UnitOfWorkManagerExtensions.RequiredTransactional(), ct);
    }

    public async Task<PaymentResult> ProcessWithResultAsync(PaymentDto dto, CancellationToken ct)
    {
        // Execute with return value
        return await _uowManager.ExecuteInUowAsync(async _ =>
        {
            var payment = new Payment(dto.Amount, dto.Currency);
            await _paymentRepo.InsertAsync(payment, saveChanges: false);
            return new PaymentResult { PaymentId = payment.Id, Success = true };
        }, UnitOfWorkManagerExtensions.RequiredTransactional(), ct);
    }
}
```

**Available Helper Methods:**

```csharp
// Predefined options
var required = UnitOfWorkManagerExtensions.RequiredTransactional();
var requiresNew = UnitOfWorkManagerExtensions.RequiresNewTransactional();
var suppressed = UnitOfWorkManagerExtensions.Suppressed();

// Execute action
await _uowManager.ExecuteInUowAsync(
    action: async ct => { /* your code */ },
    options: required,
    cancellationToken: ct);

// Execute with result
var result = await _uowManager.ExecuteInUowAsync<TResult>(
    action: async ct => { /* your code */ return result; },
    options: required,
    cancellationToken: ct);
```

## UnitOfWorkMiddleware

Automatically manage UoW for HTTP requests with configurable filtering:

```csharp
// Startup configuration
builder.Services.AddAetherUnitOfWorkMiddleware(options =>
{
    // Exclude read-only HTTP methods
    options.ExcludedHttpMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "OPTIONS", "HEAD"
    };

    // Exclude specific paths (supports wildcards)
    options.ExcludedPaths = new List<string>
    {
        "/health",
        "/healthz",
        "/metrics",
        "/_framework/*",      // Blazor framework files
        "/swagger/*",         // Swagger UI
        "/api/*/websocket"    // WebSocket endpoints
    };

    // Transaction settings
    options.IsTransactional = true;
    options.IsolationLevel = IsolationLevel.ReadCommitted;

    // Custom filter (takes precedence over ExcludedHttpMethods/ExcludedPaths)
    options.ShouldStartUnitOfWork = context =>
    {
        // Custom logic to determine if UoW should start
        return context.Request.Path.StartsWithSegments("/api")
            && context.Request.Method != "GET";
    };
});

// Apply middleware
app.UseAetherUnitOfWork();
```

**Benefits:**
- Automatically commits on successful responses
- Automatically rolls back on exceptions
- No need for `[UnitOfWork]` attributes in controllers
- Centralized UoW configuration

## Console/Worker Applications

For non-web applications, manually set the ambient service provider:

```csharp
public class PaymentWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = _serviceProvider.CreateScope();
            
            // Set ambient service provider for this scope
            AmbientServiceProvider.Current = scope.ServiceProvider;

            try
            {
                var paymentService = scope.ServiceProvider.GetRequiredService<IPaymentService>();
                
                // Now aspects will work
                await paymentService.ProcessPendingPaymentsAsync(stoppingToken);
            }
            finally
            {
                AmbientServiceProvider.Current = null;
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
```

## Custom Aspects

Create your own aspects by extending `AetherMethodInterceptionAspect`:

```csharp
[PSerializable]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public class PerformanceMonitoringAttribute : AetherMethodInterceptionAspect
{
    public override async Task OnInvokeAsync(MethodInterceptionArgs args)
    {
        var stopwatch = Stopwatch.StartNew();
        var methodName = args.Method.Name;

        await OnBeforeAsync(args);

        try
        {
            await args.ProceedAsync();
            stopwatch.Stop();

            var logger = GetServiceProvider().GetService<ILogger>();
            logger?.LogInformation(
                "Method {Method} completed in {ElapsedMs}ms",
                methodName,
                stopwatch.ElapsedMilliseconds);

            await OnAfterAsync(args);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            await OnExceptionAsync(args, ex);
            throw;
        }
    }
}

// Usage
public class ReportService
{
    [PerformanceMonitoring]
    public virtual async Task GenerateReportAsync(CancellationToken ct)
    {
        // Your logic here
    }
}
```

## Best Practices

1. **Virtual Methods**: Always mark methods with aspects as `virtual` for PostSharp to intercept them
2. **Async Methods**: Prefer async methods with aspects for better performance
3. **CancellationToken**: Always include `CancellationToken` parameter in async methods
4. **Scope Choice**: 
   - Use `Required` for most cases (default)
   - Use `RequiresNew` when you need isolation from outer transactions
   - Use `Suppress` for read-only operations
5. **SaveChanges**: Set `saveChanges: false` in repository methods when using UoW
6. **Exception Handling**: Let exceptions bubble up; UoW will auto-rollback

## Troubleshooting

### "AmbientServiceProvider.Current not set" Error

**Cause**: AmbientServiceProvider middleware not registered or not called before aspect execution.

**Solution**: 
```csharp
// Add this in Program.cs
builder.Services.AddAetherAmbientServiceProvider();
app.UseAetherAmbientServiceProvider();
```

### Aspect Not Working

**Cause**: Method is not `virtual` or class is `sealed`.

**Solution**: Mark method as `virtual` and ensure class is not `sealed`:
```csharp
public class MyService // Remove 'sealed' if present
{
    [UnitOfWork]
    public virtual async Task MyMethodAsync() // Add 'virtual'
    {
        // ...
    }
}
```

### Transaction Not Rolling Back

**Cause**: Exception is caught and not re-thrown.

**Solution**: Let exceptions propagate or manually call `RollbackAsync()`:
```csharp
[UnitOfWork]
public virtual async Task ProcessAsync()
{
    try
    {
        // Your logic
    }
    catch (Exception ex)
    {
        // Log but don't swallow
        _logger.LogError(ex, "Error occurred");
        throw; // Re-throw to trigger rollback
    }
}
```

## Performance Considerations

- Aspects have minimal overhead (~microseconds per call)
- UoW commits are batched automatically
- Domain events are dispatched after successful commit
- Use `Suppress` scope for read-only operations to avoid unnecessary transaction overhead

## Additional Resources

- [Unit of Work Pattern Documentation](./UnitOfWork_Implementation_Summary.md)
- [Domain Events Documentation](./framework/docs/EventBus-Usage.md)
- [Repository Pattern Documentation](./framework/docs/Repostiroy-Interfaces.md)

