# Result Pattern

## Overview

Type-safe error handling without exceptions. Result pattern provides explicit success/failure states, automatic exception conversion, and railway-oriented programming for clean error propagation.

## Quick Start

### Basic Usage

```csharp
// Creating results
var success = Result<User>.Ok(user);
var failure = Result<User>.Fail(Error.NotFound("user_not_found", "User not found"));

// Checking results
if (result.IsSuccess)
{
    var user = result.Value;
}
else
{
    var error = result.Error;
}
```

### Exception-Safe Operations

```csharp
// Wrap operations that may throw
var result = await ResultExtensions.TryAsync(async ct => 
    await _dbContext.Users.FindAsync(id, ct));

// With custom error mapping
var result = await ResultExtensions.TryAsync(
    async ct => await _externalService.CallAsync(ct),
    errorMapper: ex => Error.Dependency("service_failed", ex.Message));
```

## Error Types

```csharp
// Validation errors (400)
Error.Validation("invalid_email", "Email format is invalid", "email");

// Not found (404)
Error.NotFound("user_not_found", "User not found");

// Conflict (409)
Error.Conflict("duplicate_email", "Email already exists");

// Authorization (401/403)
Error.Unauthorized("not_authenticated", "Please login");
Error.Forbidden("insufficient_permissions", "Access denied");

// Infrastructure (500/502/503)
Error.Failure("unexpected_error", "An error occurred");
Error.Dependency("database_error", "Database unavailable");
Error.Transient("service_timeout", "Temporarily unavailable");
```

## ResultExtensions API Reference

### Exception Handling

#### Try / TryAsync

Wraps potentially throwing operations in a Result:

```csharp
// Synchronous
var result = ResultExtensions.Try(() => ParseJson(input));

// Async with CancellationToken
var result = await ResultExtensions.TryAsync(
    async ct => await _repository.GetAsync(id, ct),
    cancellationToken);

// With custom error mapper
var result = await ResultExtensions.TryAsync(
    async ct => await _httpClient.GetAsync(url, ct),
    errorMapper: ex => Error.Dependency("http_error", ex.Message));
```

#### ToException / ThrowAsException

Convert Error to exception:

```csharp
// Get exception without throwing
AetherException exception = error.ToException();

// Convert and throw immediately
error.ThrowAsException(); // Always throws
```

#### ThrowIfFailure

Throw only if result is failure:

```csharp
result.ThrowIfFailure(); // Does nothing if success

// For generic Result<T>
var typedResult = await GetUserAsync(id);
typedResult.ThrowIfFailure();
var user = typedResult.Value; // Safe to access
```

#### GetValueOrThrow

Get value or throw if failure:

```csharp
var user = result.GetValueOrThrow(); // Throws AetherException if failed
```

### Monadic Operations

#### Map / MapAsync

Transform success value without changing Result structure:

```csharp
// Sync
var nameResult = userResult.Map(user => user.Name);

// Async mapper
var dtoResult = await userResult.MapAsync(async user => 
    await EnrichUserAsync(user));

// Chain on Task<Result<T>>
var result = await GetUserAsync(id)
    .MapAsync(user => user.ToDto());
```

#### Bind / BindAsync

Chain Result-returning operations (flatMap):

```csharp
// Sync
var orderResult = userResult.Bind(user => GetUserOrders(user.Id));

// Async
var result = await GetUserAsync(id)
    .BindAsync(user => ValidateUserAsync(user))
    .BindAsync(user => ProcessUserAsync(user));
```

#### Tap / TapAsync

Execute side effects without changing result:

```csharp
// Sync
var result = userResult.Tap(user => Console.WriteLine(user.Name));

// Async
var result = await GetUserAsync(id)
    .TapAsync(async user => await LogAccessAsync(user.Id));
```

#### Ensure / EnsureAsync

Validate with predicate, fail if condition not met:

```csharp
// Sync
var result = userResult.Ensure(
    user => user.IsActive, 
    Error.Forbidden("inactive", "User is inactive"));

// Async predicate
var result = await GetUserAsync(id)
    .EnsureAsync(
        async user => await HasPermissionAsync(user.Id),
        Error.Forbidden("no_permission", "Access denied"));
```

#### OnSuccess / OnFailure

Execute side effects based on result state:

```csharp
var result = await GetOrderAsync(id)
    .OnSuccess(order => _logger.LogInformation("Found order {Id}", order.Id))
    .OnFailure(error => _logger.LogWarning("Order not found: {Code}", error.Code));
```

#### Match / MatchAsync

Pattern match to different return types:

```csharp
// Sync
string message = result.Match(
    onSuccess: user => $"Welcome {user.Name}!",
    onFailure: error => $"Error: {error.Message}");

// Async success handler
var response = await GetUserAsync(id)
    .MatchAsync(
        onSuccess: async user => await BuildResponseAsync(user),
        onFailure: error => ErrorResponse.FromError(error));

// Both async
var response = await GetUserAsync(id)
    .MatchAsync(
        onSuccess: async user => await BuildResponseAsync(user),
        onFailure: async error => await BuildErrorResponseAsync(error));
```

### Railway-Oriented Programming

#### ThenAsync

Chain operations with automatic error propagation:

```csharp
var result = await GetWorkflowAsync()
    .ThenAsync(workflow => CreateInstanceAsync(workflow))
    .ThenAsync(instance => ExecuteTransitionAsync(instance))
    .ThenAsync(output => BuildResponseAsync(output));

// Sync next step
var result = await GetDataAsync()
    .ThenAsync(data => ValidateSync(data));  // sync binder

// Non-generic Result chain
await ValidateSchemaAsync(context)
    .ThenAsync(() => ValidatePolicyAsync(context));
```

#### OrElseAsync

Provide fallback on failure:

```csharp
var result = await GetCachedDataAsync()
    .OrElseAsync(error => GetFreshDataAsync());
```

#### CompensateAsync

Recover from failure with alternative value:

```csharp
var result = await GetPrimaryDataAsync()
    .CompensateAsync(error => GetDefaultDataAsync());
```

#### OnSuccessAsync / OnFailureAsync

Async side effects in railway chains:

```csharp
var result = await CreateOrderAsync(dto)
    .OnSuccessAsync(async order => await _eventBus.PublishAsync(new OrderCreatedEvent(order)))
    .OnSuccessAsync(async order => await _logger.LogAsync("Order created"))
    .OnFailureAsync(async error => await _alertService.SendAsync(error.Message));
```

### Combining Results

#### Combine

Combine multiple non-generic Results:

```csharp
var result = ResultExtensions.Combine(
    ValidateEmail(email),
    ValidatePassword(password),
    ValidateAge(age));
// Returns first error or Ok if all succeed
```

#### WhenAll / WhenAllAsync

Combine multiple generic Results into array:

```csharp
// Sync
var results = new[] { GetUser(1), GetUser(2), GetUser(3) };
var allUsersResult = ResultExtensions.WhenAll(results);
// Result<User[]> - first error or array of all values

// Async
var tasks = userIds.Select(id => GetUserAsync(id));
var allUsersResult = await ResultExtensions.WhenAllAsync(tasks);
```

### Value Extraction

#### ValueOrDefault

Get value or default:

```csharp
var user = result.ValueOrDefault();           // null if failed
var user = result.ValueOrDefault(User.Guest); // custom default
```

#### ValueOr / ValueOrAsync

Get value or compute alternative:

```csharp
// Sync factory
var user = result.ValueOr(() => CreateGuestUser());

// Async factory
var user = await resultTask.ValueOrAsync(async () => 
    await CreateGuestUserAsync());
```

#### Unwrap

Get value or throw InvalidOperationException:

```csharp
var user = result.Unwrap(); // Throws if failed (use sparingly)
```

### Null Handling

#### ToResult

Convert nullable to Result:

```csharp
// Reference types
User? user = GetNullableUser();
var result = user.ToResult(Error.NotFound("not_found", "User not found"));

// Value types
int? count = GetNullableCount();
var result = count.ToResult(Error.Validation("required", "Count is required"));
```

#### EnsureNotNull / EnsureNotNullAsync

Validate not null in chains:

```csharp
// Sync
var result = GetNullableUser().EnsureNotNull(
    Error.NotFound("not_found", "User not found"));

// Async
var result = await GetNullableUserAsync()
    .EnsureNotNullAsync(Error.NotFound("not_found", "User not found"));
```

### Type Conversion

#### ToResult<T>

Convert failed Result to different type:

```csharp
// Non-generic to generic (only for failures)
Result result = Result.Fail(error);
Result<User> userResult = result.ToResult<User>();

// Generic to different generic (only for failures)
Result<Order> orderResult = userResult.ToResult<User, Order>();
```

## Exception Integration

Aether exceptions are automatically converted to appropriate Error types:

| Exception | Error Type |
|-----------|-----------|
| `EntityNotFoundException` | `Error.NotFound` |
| `AetherValidationException` | `Error.Validation` |
| `IBusinessException` | `Error.Forbidden` |
| `AetherDbConcurrencyException` | `Error.Conflict` |
| `OperationCanceledException` | `Error.Transient` |
| `IUserFriendlyException` | `Error.Failure` |
| `NotImplementedException` | `Error.Failure` |

## ASP.NET Core Integration

### Controller Usage

```csharp
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
```

### Error to HTTP Status Mapping

| Error Type | HTTP Status |
|-----------|-------------|
| Validation | 400 Bad Request |
| Unauthorized | 401 Unauthorized |
| Forbidden | 403 Forbidden |
| NotFound | 404 Not Found |
| Conflict | 409 Conflict |
| Failure | 500 Internal Server Error |
| Dependency | 502 Bad Gateway |
| Transient | 503 Service Unavailable |

## Complete Example

```csharp
public class OrderService
{
    public async Task<Result<OrderDto>> CreateOrderAsync(CreateOrderDto dto)
    {
        return await ValidateOrderAsync(dto)
            .ThenAsync(validDto => ResultExtensions.TryAsync(async ct =>
            {
                var order = new Order(validDto);
                await _repository.InsertAsync(order, ct);
                return order;
            }))
            .OnSuccessAsync(order => _eventBus.PublishAsync(new OrderCreatedEvent(order.Id)))
            .OnFailureAsync(error => _logger.LogWarning("Order creation failed: {Code}", error.Code))
            .MapAsync(order => _mapper.Map<OrderDto>(order));
    }
    
    private Result<CreateOrderDto> ValidateOrderAsync(CreateOrderDto dto)
    {
        if (dto.Items.Count == 0)
            return Result<CreateOrderDto>.Fail(
                Error.Validation("empty_order", "Order must have items"));
        
        return Result<CreateOrderDto>.Ok(dto);
    }
}
```

## Best Practices

1. **Use Result for business logic** - Explicit error handling for validation and rules
2. **Use TryAsync for infrastructure** - Wrap database, HTTP, external service calls
3. **Chain with railway operators** - Use `ThenAsync`, avoid manual `if (!result.IsSuccess)` checks
4. **Provide meaningful error codes** - `EMAIL_INVALID_FORMAT` not `error`
5. **Convert to ActionResult at controller** - Use `ToActionResult()` extension

## Related Features

- [Aspects](../aspects/README.md) - Error handling in aspects
- [Application Services](../application-services/README.md) - Service layer patterns
