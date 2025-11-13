# HTTP Client Wrapper

## Overview

Aether provides a typed HTTP client abstraction with configuration-based setup, authentication strategies, and consistent error handling. It simplifies external API integration with strongly-typed clients.

## Key Features

- **Typed Clients** - Strongly-typed HTTP client wrappers
- **Configuration-Based** - Endpoint configuration in appsettings
- **Authentication Strategies** - Pluggable authentication
- **Token Management** - Automatic token handling
- **Retry Policies** - Built-in resilience (with Polly)
- **Timeout Configuration** - Per-client timeout settings

## Core Interface

### IHttpClientWrapper

```csharp
public interface IHttpClientWrapper
{
    Task<TResponse?> GetAsync<TResponse>(
        string endpoint, 
        CancellationToken cancellationToken = default);
    
    Task<TResponse?> PostAsync<TRequest, TResponse>(
        string endpoint, 
        TRequest request, 
        CancellationToken cancellationToken = default);
    
    Task<TResponse?> PutAsync<TRequest, TResponse>(
        string endpoint, 
        TRequest request, 
        CancellationToken cancellationToken = default);
    
    Task DeleteAsync(
        string endpoint, 
        CancellationToken cancellationToken = default);
}
```

## Configuration

### appsettings.json

```json
{
  "ApiEndpoints": {
    "PaymentApi": {
      "BaseUrl": "https://api.payment-provider.com",
      "DefaultTimeOut": 30,
      "DefaultMediaTypeWithQualityHeaderValue": "application/json",
      "DefaultRequestHeaders": {
        "X-Api-Version": "2.0",
        "X-Client-Id": "my-app"
      }
    },
    "NotificationApi": {
      "BaseUrl": "https://api.notifications.com",
      "DefaultTimeOut": 10,
      "DefaultMediaTypeWithQualityHeaderValue": "application/json"
    }
  }
}
```

### Service Registration

```csharp
services.RegisterHttpClient<IPaymentApiClient, PaymentApiHttpClient>();
services.RegisterHttpClient<INotificationApiClient, NotificationApiHttpClient>();
```

## Usage Examples

### Defining a Client

```csharp
// Interface
public interface IPaymentApiClient : IHttpClientWrapper
{
    Task<PaymentResponse> ProcessPaymentAsync(
        ProcessPaymentRequest request, 
        CancellationToken cancellationToken = default);
    
    Task<Payment> GetPaymentAsync(
        string paymentId, 
        CancellationToken cancellationToken = default);
}

// Implementation
public class PaymentApiHttpClient : HttpClientWrapper, IPaymentApiClient
{
    public PaymentApiHttpClient(
        IHttpClientFactory httpClientFactory,
        IAuthenticationStrategyFactory authStrategyFactory,
        ILogger<PaymentApiHttpClient> logger)
        : base(httpClientFactory, authStrategyFactory, logger)
    {
    }
    
    public async Task<PaymentResponse> ProcessPaymentAsync(
        ProcessPaymentRequest request,
        CancellationToken cancellationToken = default)
    {
        return await PostAsync<ProcessPaymentRequest, PaymentResponse>(
            "payments/process",
            request,
            cancellationToken);
    }
    
    public async Task<Payment> GetPaymentAsync(
        string paymentId,
        CancellationToken cancellationToken = default)
    {
        return await GetAsync<Payment>(
            $"payments/{paymentId}",
            cancellationToken);
    }
}
```

### Using the Client

```csharp
public class OrderService
{
    private readonly IPaymentApiClient _paymentClient;
    
    public OrderService(IPaymentApiClient paymentClient)
    {
        _paymentClient = paymentClient;
    }
    
    public async Task ProcessOrderAsync(Order order)
    {
        var paymentRequest = new ProcessPaymentRequest
        {
            Amount = order.TotalAmount,
            Currency = "USD",
            OrderId = order.Id.ToString()
        };
        
        var result = await _paymentClient.ProcessPaymentAsync(paymentRequest);
        
        if (result.Success)
        {
            order.MarkAsPaid();
            await _orderRepository.UpdateAsync(order);
        }
    }
}
```

## Authentication

### Authentication Strategies

```csharp
public interface IAuthenticationStrategy
{
    Task AuthenticateAsync(HttpClient client, CancellationToken cancellationToken = default);
}
```

### Bearer Token Authentication

```csharp
public class BearerTokenAuthStrategy : IAuthenticationStrategy
{
    private readonly ITokenService _tokenService;
    
    public async Task AuthenticateAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var token = await _tokenService.GetAccessTokenAsync(cancellationToken);
        client.DefaultRequestHeaders.Authorization = 
            new AuthenticationHeaderValue("Bearer", token);
    }
}
```

### API Key Authentication

```csharp
public class ApiKeyAuthStrategy : IAuthenticationStrategy
{
    private readonly string _apiKey;
    
    public ApiKeyAuthStrategy(IConfiguration configuration)
    {
        _apiKey = configuration["ApiKeys:PaymentApi"];
    }
    
    public Task AuthenticateAsync(HttpClient client, CancellationToken cancellationToken)
    {
        client.DefaultRequestHeaders.Add("X-Api-Key", _apiKey);
        return Task.CompletedTask;
    }
}
```

### Token Service

```csharp
public interface ITokenService
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
    Task RefreshTokenAsync(CancellationToken cancellationToken = default);
}

public class TokenService : ITokenService
{
    private string? _cachedToken;
    private DateTime _tokenExpiry;
    
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_cachedToken != null && DateTime.UtcNow < _tokenExpiry)
        {
            return _cachedToken;
        }
        
        await RefreshTokenAsync(cancellationToken);
        return _cachedToken!;
    }
    
    public async Task RefreshTokenAsync(CancellationToken cancellationToken)
    {
        // Fetch new token from auth server
        var tokenResponse = await FetchTokenAsync(cancellationToken);
        _cachedToken = tokenResponse.AccessToken;
        _tokenExpiry = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn - 60);
    }
}
```

## Advanced Features

### Custom Headers

```csharp
public class CustomHeaderHttpClient : HttpClientWrapper
{
    protected override async Task<TResponse?> ExecuteRequestAsync<TResponse>(
        Func<HttpClient, Task<HttpResponseMessage>> request,
        CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString();
        
        return await ExecuteWithHeadersAsync<TResponse>(
            request,
            new Dictionary<string, string>
            {
                ["X-Correlation-Id"] = correlationId,
                ["X-Request-Id"] = Guid.NewGuid().ToString()
            },
            cancellationToken);
    }
}
```

### Retry Policy

```csharp
services.AddHttpClient<PaymentApiHttpClient>()
    .AddTransientHttpErrorPolicy(policy =>
        policy.WaitAndRetryAsync(
            retryCount: 3,
            sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
            onRetry: (outcome, timespan, retryCount, context) =>
            {
                logger.LogWarning("Retry {RetryCount} after {Delay}s", retryCount, timespan.TotalSeconds);
            }));
```

### Circuit Breaker

```csharp
services.AddHttpClient<PaymentApiHttpClient>()
    .AddTransientHttpErrorPolicy(policy =>
        policy.CircuitBreakerAsync(
            handledEventsAllowedBeforeBreaking: 5,
            durationOfBreak: TimeSpan.FromSeconds(30),
            onBreak: (outcome, duration) =>
            {
                logger.LogError("Circuit breaker opened for {Duration}s", duration.TotalSeconds);
            },
            onReset: () =>
            {
                logger.LogInformation("Circuit breaker reset");
            }));
```

## Error Handling

### Custom Error Handling

```csharp
public class PaymentApiHttpClient : HttpClientWrapper
{
    protected override async Task HandleErrorResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new PaymentValidationException(error);
        }
        
        if (response.StatusCode == HttpStatusCode.PaymentRequired)
        {
            throw new InsufficientFundsException();
        }
        
        await base.HandleErrorResponseAsync(response, cancellationToken);
    }
}
```

## Best Practices

### 1. Use Interface for Abstraction

```csharp
// ✅ Good: Interface-based
public interface IPaymentApiClient : IHttpClientWrapper
{
    Task<PaymentResponse> ProcessPaymentAsync(ProcessPaymentRequest request);
}

// Register interface
services.RegisterHttpClient<IPaymentApiClient, PaymentApiHttpClient>();

// Inject interface
public OrderService(IPaymentApiClient paymentClient) { }
```

### 2. Configure Timeouts Appropriately

```json
{
  "ApiEndpoints": {
    "FastApi": {
      "DefaultTimeOut": 5
    },
    "SlowApi": {
      "DefaultTimeOut": 60
    }
  }
}
```

### 3. Implement Retry with Backoff

```csharp
services.AddHttpClient<MyApiClient>()
    .AddTransientHttpErrorPolicy(policy =>
        policy.WaitAndRetryAsync(3, attempt => 
            TimeSpan.FromSeconds(Math.Pow(2, attempt))));
```

### 4. Cache Tokens

```csharp
// ✅ Good: Token caching
private string? _cachedToken;

public async Task<string> GetTokenAsync()
{
    if (_cachedToken != null && !IsExpired())
        return _cachedToken;
    
    _cachedToken = await FetchNewTokenAsync();
    return _cachedToken;
}
```

## Testing

### Mocking HTTP Client

```csharp
public class OrderServiceTests
{
    private readonly Mock<IPaymentApiClient> _mockPaymentClient;
    private readonly OrderService _service;
    
    [Fact]
    public async Task ProcessOrder_ShouldCallPaymentApi()
    {
        // Arrange
        var order = new Order { TotalAmount = 100m };
        _mockPaymentClient
            .Setup(c => c.ProcessPaymentAsync(It.IsAny<ProcessPaymentRequest>(), default))
            .ReturnsAsync(new PaymentResponse { Success = true });
        
        // Act
        await _service.ProcessOrderAsync(order);
        
        // Assert
        _mockPaymentClient.Verify(
            c => c.ProcessPaymentAsync(
                It.Is<ProcessPaymentRequest>(r => r.Amount == 100m),
                default),
            Times.Once);
    }
}
```

## Related Features

- **[Distributed Events](../distributed-events/README.md)** - Event-based integration
- **[Response Compression](../response-compression/README.md)** - API performance

## Common Issues

### Issue: Timeout exceptions

**Solution:** Increase timeout or optimize API:

```json
{
  "ApiEndpoints": {
    "SlowApi": {
      "DefaultTimeOut": 120
    }
  }
}
```

### Issue: Authentication failures

**Solution:** Verify token service and credentials:

```csharp
_logger.LogDebug("Token: {Token}", await _tokenService.GetAccessTokenAsync());
```

### Issue: Deserialization errors

**Solution:** Ensure matching DTO properties:

```csharp
public class PaymentResponse
{
    [JsonPropertyName("payment_id")]
    public string PaymentId { get; set; }
}
```

