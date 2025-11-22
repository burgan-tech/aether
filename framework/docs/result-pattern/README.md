# Result Pattern

## Overview

Aether provides a comprehensive Result pattern implementation that enables exception-free error handling, functional programming patterns, and railway-oriented programming. The Result pattern is a type-safe way to represent success or failure of operations without throwing exceptions.

The framework includes:
- **Type-Safe Error Handling** - Explicit success/failure states
- **Exception-to-Error Conversion** - Automatic exception handling with Try methods
- **Monadic Operations** - Map, Bind, Tap for functional composition
- **Railway-Oriented Programming** - Chain operations with automatic error propagation
- **Async Support** - Full async/await integration
- **Rich Error Types** - Structured error information with codes, messages, and validation details

## Architecture

### Core Types

```
Result (non-generic)
  ├── IsSuccess: bool
  └── Error: Error

Result<T> (generic)
  ├── IsSuccess: bool
  ├── Value: T?
  └── Error: Error

Error (record struct)
  ├── Prefix: string (validation, conflict, notfound, etc.)
  ├── Code: string
  ├── Message: string?
  ├── Detail: string?
  ├── Target: string?
  └── ValidationErrors: IList<ValidationResult>?
```

## Quick Start

### Basic Usage

```csharp
// Creating results
var success = Result.Ok();
var successWithValue = Result<User>.Ok(user);
var failure = Result.Fail(Error.Validation("invalid_input", "Invalid user data"));
var failureWithValue = Result<User>.Fail(Error.NotFound("user_not_found", "User not found"));

// Checking results
if (result.IsSuccess)
{
    var value = result.Value;
    // Process value
}
else
{
    var error = result.Error;
    // Handle error
}
```

### Exception Handling with Try

The Try methods automatically convert exceptions to appropriate Error types based on Aether's exception hierarchy:

```csharp
// Synchronous operation
var result = ResultExtensions.Try(() => 
{
    return userService.GetUser(id); // May throw exceptions
});

// Async operation
var result = await ResultExtensions.TryAsync(async ct => 
{
    return await userService.GetUserAsync(id, ct);
});

// With custom error mapping
var result = ResultExtensions.Try(
    () => externalService.Call(),
    ex => Error.Dependency("external_service_failed", ex.Message)
);
```

## Error Types

### Creating Errors

```csharp
// Validation errors
Error.Validation("invalid_email", "Email format is invalid", "email");
Error.Validation("validation_failed", "Validation failed", validationErrors);

// Not found errors
Error.NotFound("user_not_found", "User not found", userId.ToString());

// Conflict errors
Error.Conflict("duplicate_email", "Email already exists", "email");

// Authorization errors
Error.Unauthorized("not_authenticated", "Please login");
Error.Forbidden("insufficient_permissions", "You don't have permission");

// Business rule errors
Error.Forbidden("business_rule_violation", "Cannot delete published post");

// Transient errors (retry-able)
Error.Transient("service_timeout", "Service temporarily unavailable");

// Dependency errors (external service failures)
Error.Dependency("database_error", "Database connection failed");

// General failures
Error.Failure("unexpected_error", "An unexpected error occurred", details);
```

### Error Codes

Aether provides predefined error code constants:

```csharp
ErrorCodes.Prefixes.Validation    // "validation"
ErrorCodes.Prefixes.Conflict      // "conflict"
ErrorCodes.Prefixes.NotFound      // "notfound"
ErrorCodes.Prefixes.Unauthorized  // "unauthorized"
ErrorCodes.Prefixes.Forbidden     // "forbidden"
ErrorCodes.Prefixes.Dependency    // "dependency"
ErrorCodes.Prefixes.Transient     // "transient"
ErrorCodes.Prefixes.Failure       // "failure"

ErrorCodes.Auth.Unauthenticated           // "Unauthenticated"
ErrorCodes.Auth.InvalidCredentials        // "InvalidCredentials"
ErrorCodes.Auth.TokenExpired              // "TokenExpired"
ErrorCodes.Auth.InsufficientPermissions   // "InsufficientPermissions"

ErrorCodes.Validation.ModelValidationFailed // "ModelValidation:Failed"
ErrorCodes.Validation.Required              // "Required"
ErrorCodes.Validation.InvalidFormat         // "InvalidFormat"
ErrorCodes.Validation.OutOfRange            // "OutOfRange"
```

## Exception Handling Integration

### Automatic Exception-to-Error Conversion

The Result pattern integrates with Aether's exception handling system to automatically convert exceptions to appropriate Error types:

| Exception Type | Mapped To | Error Code |
|---------------|-----------|------------|
| `OperationCanceledException` | `Error.Transient` | "operation_canceled" |
| `AetherDbConcurrencyException` | `Error.Conflict` | "concurrency" |
| `EntityNotFoundException` | `Error.NotFound` | "entity_not_found" |
| `AetherValidationException` | `Error.Validation` | "validation_failed" |
| `IUserFriendlyException` | `Error.Failure` | "user_friendly" |
| `IBusinessException` | `Error.Forbidden` | "business_rule" |
| `NotImplementedException` | `Error.Failure` | "not_implemented" |
| Other exceptions | `Error.Failure` | "unexpected" |

### Exception Interface Support

The conversion process respects Aether's exception interfaces:

- **IHasErrorCode** - Extracts custom error code
- **IHasErrorDetails** - Extracts additional details
- **IHasValidationErrors** - Preserves validation errors
- **AggregateException** - Unwraps to find Aether-specific exceptions

### Examples

```csharp
// AetherValidationException → Error.Validation with ValidationErrors
var result = ResultExtensions.Try(() => 
{
    throw new AetherValidationException("Validation failed", validationErrors);
});
// result.Error.ValidationErrors contains the validation errors
// result.Error.Code: "validation_failed"

// EntityNotFoundException → Error.NotFound with entity details
var result = await ResultExtensions.TryAsync(async ct => 
{
    throw new EntityNotFoundException(typeof(User), 123);
});
// result.Error.Message: "There is no entity User with id = 123!"
// result.Error.Code: "entity_not_found"
// result.Error.Target: "123"

// BusinessException with IHasErrorCode → Error.Forbidden with custom code
var result = ResultExtensions.Try(() => 
{
    throw new BusinessException("BUS001", "Cannot delete published post");
});
// result.Error.Code: "BUS001"
// result.Error.Message: "Cannot delete published post"

// IUserFriendlyException → Error.Failure (user-facing message)
var result = ResultExtensions.Try(() => 
{
    throw new UserFriendlyException("Your session has expired", "SESSION_EXPIRED");
});
// result.Error.Code: "SESSION_EXPIRED"
// result.Error.Message: "Your session has expired"
```

## Monadic Operations

### Map - Transform Success Value

```csharp
var userResult = await GetUserAsync(id);
var nameResult = userResult.Map(user => user.Name);

// Async version
var nameResult = await GetUserAsync(id)
    .MapAsync(user => user.Name);

// With async mapper
var emailResult = await GetUserAsync(id)
    .MapAsync(async user => await GetPrimaryEmailAsync(user.Id));
```

### Bind - Chain Result-Returning Operations

```csharp
var result = GetUserAsync(id)
    .Bind(user => ValidateUser(user))
    .Bind(user => GetUserOrders(user.Id));

// Async version
var result = await GetUserAsync(id)
    .BindAsync(user => ValidateUserAsync(user))
    .BindAsync(user => GetUserOrdersAsync(user.Id));
```

### Tap - Side Effects Without Changing Result

```csharp
var result = await GetUserAsync(id)
    .Tap(user => logger.LogInformation("Found user: {Name}", user.Name))
    .TapAsync(async user => await auditService.LogAccessAsync(user.Id));
```

### Ensure - Validate with Predicates

```csharp
var result = await GetUserAsync(id)
    .Ensure(user => user.IsActive, Error.Validation("inactive_user", "User is not active"))
    .EnsureAsync(user => user.Age >= 18, Error.Validation("underage", "Must be 18+"));
```

### Match - Pattern Matching

```csharp
var message = result.Match(
    onSuccess: user => $"Welcome {user.Name}!",
    onFailure: error => $"Error: {error.Message}"
);

// Async version
var response = await GetUserAsync(id)
    .MatchAsync(
        user => new { Success = true, Data = user },
        error => new { Success = false, Error = error.Message }
    );
```

## Railway-Oriented Programming

Railway-oriented programming treats operations as a railway track where success stays on the "happy path" and errors automatically switch to the "error track."

### ThenAsync - Chain Operations

```csharp
var result = await GetWorkflowAsync()
    .ThenAsync(workflow => CreateInstanceAsync(workflow))
    .ThenAsync(instance => ExecuteTransitionAsync(instance))
    .ThenAsync(output => BuildResponseAsync(output));

// If any step fails, subsequent steps are skipped and error is propagated
```

### OrElseAsync - Fallback Operations

```csharp
var result = await GetCachedDataAsync()
    .OrElseAsync(error => GetFreshDataAsync());

// If GetCachedDataAsync fails, try GetFreshDataAsync
```

### CompensateAsync - Error Recovery

```csharp
var result = await GetPrimaryServiceDataAsync()
    .CompensateAsync(error => GetDefaultData());

// If primary service fails, provide default data
```

### OnSuccessAsync / OnFailureAsync - Side Effects

```csharp
var result = await CreateUserAsync(dto)
    .OnSuccessAsync(user => logger.LogInformation("User created: {Id}", user.Id))
    .OnSuccessAsync(user => eventBus.PublishAsync(new UserCreatedEvent(user)))
    .OnFailureAsync(error => logger.LogError("User creation failed: {Error}", error.Code));
```

### Complex Example

```csharp
public async Task<Result<OrderDto>> ProcessOrderAsync(CreateOrderDto dto)
{
    return await ResultExtensions.TryAsync(async ct => 
        {
            return await ValidateOrderAsync(dto, ct);
        })
        .ThenAsync(order => CheckInventoryAsync(order))
        .ThenAsync(order => CalculatePricingAsync(order))
        .ThenAsync(order => ApplyDiscountsAsync(order))
        .ThenAsync(order => CreateOrderAsync(order))
        .OnSuccessAsync(order => logger.LogInformation("Order created: {Id}", order.Id))
        .OnSuccessAsync(order => eventBus.PublishAsync(new OrderCreatedEvent(order)))
        .OnFailureAsync(error => logger.LogWarning("Order failed: {Error}", error.Code))
        .CompensateAsync(error => GetLastOrderAsync())
        .MapAsync(order => order.ToDto());
}
```

## Combining Results

### Combine - Multiple Non-Generic Results

```csharp
var result1 = ValidateEmail(email);
var result2 = ValidatePassword(password);
var result3 = ValidateAge(age);

var combinedResult = ResultExtensions.Combine(result1, result2, result3);
// Success only if all results are successful
// Returns first error encountered
```

### WhenAll - Multiple Generic Results

```csharp
var results = new[] 
{
    GetUserAsync(1),
    GetUserAsync(2),
    GetUserAsync(3)
};

var allUsersResult = ResultExtensions.WhenAll(results);
// Success: Result<User[]> with all users
// Failure: Returns first error encountered
```

### WhenAllAsync - Async Operations

```csharp
var tasks = userIds.Select(id => GetUserAsync(id));
var allUsersResult = await ResultExtensions.WhenAllAsync(tasks);

if (allUsersResult.IsSuccess)
{
    var users = allUsersResult.Value; // User[]
    // Process all users
}
```

## Value Extraction

### GetValueOrThrow

```csharp
try 
{
    var user = result.GetValueOrThrow();
    // Use user
}
catch (AetherException ex)
{
    // Handle exception created from Error
}
```

### ValueOrDefault

```csharp
var user = result.ValueOrDefault();
var user = result.ValueOrDefault(User.Guest);
```

### ValueOr - Factory Function

```csharp
var user = result.ValueOr(() => CreateGuestUser());

// Async version
var user = await resultTask.ValueOrAsync(async () => await CreateGuestUserAsync());
```

### Unwrap - Throws InvalidOperationException

```csharp
var user = result.Unwrap(); // Throws if failed
```

## Error-to-Exception Conversion

### ToException - Create Exception from Error

```csharp
var error = Error.Validation("invalid_input", "Invalid data", validationErrors);
var exception = error.ToException();
// Returns: AetherValidationException with validation errors

var error = Error.NotFound("user_not_found", "User not found");
var exception = error.ToException();
// Returns: ErrorException with code and message
```

### ThrowAsException - Throw Immediately

```csharp
if (!result.IsSuccess)
{
    result.Error.ThrowAsException(); // Throws appropriate AetherException
}
```

### ThrowIfFailure

```csharp
result.ThrowIfFailure(); // Does nothing if success, throws if failure

// Also works with Result<T>
var result = await GetUserAsync(id);
result.ThrowIfFailure();
var user = result.Value; // Safe to access
```

## Conversion Helpers

### Nullable to Result

```csharp
// Reference types
User? user = GetNullableUser();
var result = user.ToResult(Error.NotFound("user_not_found", "User not found"));

// Value types
int? age = GetNullableAge();
var result = age.ToResult(Error.Validation("age_required", "Age is required"));
```

### Result Type Conversion

```csharp
// Non-generic to generic (only for failures)
Result result = Result.Fail(error);
Result<User> userResult = result.ToResult<User>();

// Generic to different generic (only for failures)
Result<Order> orderResult = userResult.ToResult<User, Order>();
```

## ASP.NET Core Integration

### Converting to ActionResult

```csharp
[HttpGet("{id}")]
public async Task<IActionResult> GetUser(int id)
{
    var result = await userService.GetUserAsync(id);
    return result.ToActionResult(HttpContext);
    
    // Success → 200 OK with value
    // Failure → ProblemDetails with appropriate status code
}

// With custom status code
[HttpPost]
public async Task<IActionResult> CreateUser(CreateUserDto dto)
{
    var result = await userService.CreateUserAsync(dto);
    return result.ToActionResult(HttpContext, value => 201);
    
    // Success → 201 Created
    // Failure → ProblemDetails
}
```

### Error to HTTP Status Mapping

| Error Type | HTTP Status |
|-----------|-------------|
| `Validation` | 400 Bad Request |
| `Unauthorized` | 401 Unauthorized |
| `Forbidden` | 403 Forbidden |
| `NotFound` | 404 Not Found |
| `Conflict` | 409 Conflict |
| `Failure` | 500 Internal Server Error |
| `Dependency` | 502 Bad Gateway |
| `Transient` | 503 Service Unavailable |

## Best Practices

### 1. Use Result Pattern for Business Logic

```csharp
// ✅ Good - Explicit error handling
public Result<Order> CreateOrder(CreateOrderDto dto)
{
    if (dto.Items.Count == 0)
        return Result<Order>.Fail(Error.Validation("empty_order", "Order must have items"));
    
    if (dto.TotalAmount < 0)
        return Result<Order>.Fail(Error.Validation("negative_amount", "Amount cannot be negative"));
    
    var order = new Order(dto);
    return Result<Order>.Ok(order);
}

// ❌ Bad - Using exceptions for business logic
public Order CreateOrder(CreateOrderDto dto)
{
    if (dto.Items.Count == 0)
        throw new ValidationException("Order must have items");
    
    var order = new Order(dto);
    return order;
}
```

### 2. Use Try for External/Infrastructure Operations

```csharp
// ✅ Good - Wrap external calls
public async Task<Result<User>> GetUserAsync(int id)
{
    return await ResultExtensions.TryAsync(async ct => 
    {
        return await _dbContext.Users.FindAsync(id, ct);
    });
}

// ✅ Also good - Explicit error mapping
public async Task<Result<ExternalData>> GetExternalDataAsync()
{
    return await ResultExtensions.TryAsync(
        async ct => await _httpClient.GetFromJsonAsync<ExternalData>("/api/data", ct),
        ex => Error.Dependency("external_service_failed", ex.Message)
    );
}
```

### 3. Chain Operations with Railway Pattern

```csharp
// ✅ Good - Fluent error propagation
public async Task<Result<OrderDto>> ProcessOrderAsync(CreateOrderDto dto)
{
    return await ValidateOrderAsync(dto)
        .ThenAsync(order => CheckInventoryAsync(order))
        .ThenAsync(order => CalculatePricingAsync(order))
        .ThenAsync(order => SaveOrderAsync(order))
        .MapAsync(order => order.ToDto());
}

// ❌ Bad - Manual error checking
public async Task<Result<OrderDto>> ProcessOrderAsync(CreateOrderDto dto)
{
    var validateResult = await ValidateOrderAsync(dto);
    if (!validateResult.IsSuccess) return Result<OrderDto>.Fail(validateResult.Error);
    
    var inventoryResult = await CheckInventoryAsync(validateResult.Value);
    if (!inventoryResult.IsSuccess) return Result<OrderDto>.Fail(inventoryResult.Error);
    
    // ... more manual checks
}
```

### 4. Prefer Railway Operators Over Manual Checks

```csharp
// ✅ Good
var result = await GetUserAsync(id)
    .EnsureAsync(user => user.IsActive, Error.Validation("inactive", "User inactive"))
    .ThenAsync(user => ProcessUserAsync(user));

// ❌ Bad
var userResult = await GetUserAsync(id);
if (!userResult.IsSuccess) return Result.Fail(userResult.Error);
if (!userResult.Value.IsActive) return Result.Fail(Error.Validation("inactive", "User inactive"));
var processResult = await ProcessUserAsync(userResult.Value);
```

### 5. Use Match for Different Return Types

```csharp
// ✅ Good
public async Task<IActionResult> GetUser(int id)
{
    return await userService.GetUserAsync(id)
        .MatchAsync(
            user => Ok(user),
            error => error.Prefix switch
            {
                "notfound" => NotFound(error.Message),
                "validation" => BadRequest(error.Message),
                _ => StatusCode(500, error.Message)
            }
        );
}
```

### 6. Provide Meaningful Error Codes

```csharp
// ✅ Good - Specific error codes
Error.Validation("EMAIL_INVALID_FORMAT", "Email format is invalid", "email")
Error.Conflict("EMAIL_ALREADY_EXISTS", "Email already in use", "email")
Error.NotFound("USER_NOT_FOUND", "User not found", userId.ToString())

// ❌ Bad - Generic error codes
Error.Validation("error", "Something is wrong")
Error.Failure("failed", "Operation failed")
```

### 7. Preserve Validation Errors

```csharp
// ✅ Good - Preserve validation details
public Result<User> ValidateUser(CreateUserDto dto)
{
    var validationResults = new List<ValidationResult>();
    
    if (string.IsNullOrEmpty(dto.Email))
        validationResults.Add(new ValidationResult("Email is required", new[] { "Email" }));
    
    if (dto.Age < 18)
        validationResults.Add(new ValidationResult("Must be 18+", new[] { "Age" }));
    
    if (validationResults.Any())
        return Result<User>.Fail(Error.Validation("validation_failed", "Validation failed", validationResults));
    
    return Result<User>.Ok(new User(dto));
}
```

### 8. Use OnSuccess/OnFailure for Logging and Events

```csharp
public async Task<Result<Order>> CreateOrderAsync(CreateOrderDto dto)
{
    return await ResultExtensions.TryAsync(async ct => await _repository.CreateAsync(dto, ct))
        .OnSuccessAsync(order => _eventBus.PublishAsync(new OrderCreatedEvent(order)))
        .OnSuccessAsync(order => _logger.LogInformation("Order created: {OrderId}", order.Id))
        .OnFailureAsync(error => _logger.LogError("Order creation failed: {Error}", error.Code))
        .OnFailureAsync(error => _metrics.IncrementCounter("order_failures"));
}
```

## Common Patterns

### Repository Pattern

```csharp
public interface IUserRepository
{
    Task<Result<User>> GetByIdAsync(int id);
    Task<Result<User>> CreateAsync(CreateUserDto dto);
    Task<Result> UpdateAsync(User user);
    Task<Result> DeleteAsync(int id);
}

public class UserRepository : IUserRepository
{
    public async Task<Result<User>> GetByIdAsync(int id)
    {
        return await ResultExtensions.TryAsync(async ct =>
        {
            var user = await _dbContext.Users.FindAsync(id, ct);
            if (user == null)
                throw new EntityNotFoundException(typeof(User), id);
            return user;
        });
    }
}
```

### Service Layer

```csharp
public class OrderService
{
    public async Task<Result<OrderDto>> CreateOrderAsync(CreateOrderDto dto)
    {
        return await ValidateOrderAsync(dto)
            .ThenAsync(order => _inventoryService.ReserveItemsAsync(order))
            .ThenAsync(order => _pricingService.CalculateTotalAsync(order))
            .ThenAsync(order => _repository.CreateAsync(order))
            .OnSuccessAsync(order => _eventBus.PublishAsync(new OrderCreatedEvent(order)))
            .MapAsync(order => _mapper.Map<OrderDto>(order));
    }
    
    private Result<Order> ValidateOrderAsync(CreateOrderDto dto)
    {
        if (dto.Items.Count == 0)
            return Result<Order>.Fail(Error.Validation("empty_order", "Order must have items"));
        
        return Result<Order>.Ok(new Order(dto));
    }
}
```

### Controller Layer

```csharp
[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly IUserService _userService;
    
    [HttpGet("{id}")]
    public async Task<IActionResult> GetUser(int id)
    {
        var result = await _userService.GetUserAsync(id);
        return result.ToActionResult(HttpContext);
    }
    
    [HttpPost]
    public async Task<IActionResult> CreateUser(CreateUserDto dto)
    {
        var result = await _userService.CreateUserAsync(dto);
        return result.ToActionResult(HttpContext, _ => 201);
    }
    
    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateUser(int id, UpdateUserDto dto)
    {
        return await _userService.UpdateUserAsync(id, dto)
            .ThenAsync(user => _userService.NotifyUserUpdatedAsync(user))
            .MatchAsync(
                user => Ok(user),
                error => error.Prefix switch
                {
                    "notfound" => NotFound(error.Message),
                    "validation" => BadRequest(error),
                    "forbidden" => Forbid(),
                    _ => StatusCode(500, error.Message)
                }
            );
    }
}
```

## Performance Considerations

### 1. Result is a Struct
Result and Result<T> are value types (struct), which means they're stack-allocated and have minimal overhead.

### 2. Avoid Excessive Chaining
While railway operators are powerful, excessive chaining can impact readability:

```csharp
// ✅ Good - Reasonable chain
return await GetUserAsync(id)
    .ThenAsync(user => ValidateUserAsync(user))
    .ThenAsync(user => ProcessUserAsync(user));

// ⚠️ Be careful - Very long chain
return await GetUserAsync(id)
    .ThenAsync(op1).ThenAsync(op2).ThenAsync(op3)
    .ThenAsync(op4).ThenAsync(op5).ThenAsync(op6)
    .ThenAsync(op7).ThenAsync(op8); // Hard to debug
```

### 3. Use ValueOrDefault for Hot Paths
When you need to extract values frequently and have a reasonable default:

```csharp
var user = result.ValueOrDefault(User.Guest); // Faster than exception-based extraction
```

## Testing

### Testing Success Cases

```csharp
[Fact]
public async Task CreateUser_ValidData_ReturnsSuccess()
{
    // Arrange
    var dto = new CreateUserDto { Email = "test@example.com", Age = 25 };
    
    // Act
    var result = await _userService.CreateUserAsync(dto);
    
    // Assert
    Assert.True(result.IsSuccess);
    Assert.NotNull(result.Value);
    Assert.Equal("test@example.com", result.Value.Email);
}
```

### Testing Failure Cases

```csharp
[Fact]
public async Task CreateUser_InvalidEmail_ReturnsValidationError()
{
    // Arrange
    var dto = new CreateUserDto { Email = "invalid", Age = 25 };
    
    // Act
    var result = await _userService.CreateUserAsync(dto);
    
    // Assert
    Assert.False(result.IsSuccess);
    Assert.Equal("validation", result.Error.Prefix);
    Assert.Equal("EMAIL_INVALID_FORMAT", result.Error.Code);
}
```

### Testing Railway Operations

```csharp
[Fact]
public async Task ProcessOrder_InventoryFails_ReturnsConflictError()
{
    // Arrange
    var dto = new CreateOrderDto { Items = new[] { new OrderItemDto { ProductId = 1, Quantity = 100 } } };
    _inventoryService.Setup(x => x.ReserveItemsAsync(It.IsAny<Order>()))
        .ReturnsAsync(Result<Order>.Fail(Error.Conflict("insufficient_stock", "Not enough stock")));
    
    // Act
    var result = await _orderService.CreateOrderAsync(dto);
    
    // Assert
    Assert.False(result.IsSuccess);
    Assert.Equal("conflict", result.Error.Prefix);
    Assert.Equal("insufficient_stock", result.Error.Code);
}
```

## Migration Guide

### From Exception-Based to Result Pattern

#### Before (Exception-Based)

```csharp
public async Task<User> GetUserAsync(int id)
{
    var user = await _dbContext.Users.FindAsync(id);
    if (user == null)
        throw new EntityNotFoundException(typeof(User), id);
    
    if (!user.IsActive)
        throw new BusinessException("USER_INACTIVE", "User is not active");
    
    return user;
}

// Usage
try
{
    var user = await GetUserAsync(id);
    // Use user
}
catch (EntityNotFoundException ex)
{
    return NotFound(ex.Message);
}
catch (BusinessException ex)
{
    return BadRequest(ex.Message);
}
```

#### After (Result Pattern)

```csharp
public async Task<Result<User>> GetUserAsync(int id)
{
    return await ResultExtensions.TryAsync(async ct => await _dbContext.Users.FindAsync(id, ct))
        .EnsureAsync(user => user != null, Error.NotFound("user_not_found", "User not found"))
        .EnsureAsync(user => user.IsActive, Error.Forbidden("user_inactive", "User is not active"));
}

// Usage
public async Task<IActionResult> GetUser(int id)
{
    var result = await GetUserAsync(id);
    return result.ToActionResult(HttpContext);
}
```

## See Also

- [Exception Handling](../exception-handling/README.md)
- [Domain-Driven Design](../ddd/README.md)
- [Application Services](../application-services/README.md)
- [Repository Pattern](../repository-pattern/README.md)

