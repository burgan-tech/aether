# Background Jobs

## Overview

Aether's Background Job system provides scheduled job execution integrated with Dapr Jobs. It supports cron expressions, job persistence, automatic handler discovery, and retry mechanisms for building reliable background processing.

## Key Features

- **Dapr Jobs Integration** - Built on Dapr's job scheduling
- **Job Persistence** - Jobs stored in database
- **Automatic Handler Discovery** - Convention-based handler registration
- **Cron Expressions** - Flexible scheduling
- **Retry Support** - Automatic retry on failure
- **Type-Safe Handlers** - Strongly-typed job arguments

## Core Interfaces

### IBackgroundJobService

```csharp
public interface IBackgroundJobService
{
    Task<Guid> EnqueueAsync<TOpts, TJob, TArgs>(
        TOpts args, 
        CancellationToken cancellationToken = default) 
        where TJob : IBackgroundJobHandler<TArgs>;
    
    Task<bool> DeleteAsync(Guid jobId, CancellationToken cancellationToken = default);
}
```

### IBackgroundJobHandler<TArgs>

```csharp
public interface IBackgroundJobHandler<in TArgs>
{
    Task ExecuteAsync(TArgs args, CancellationToken cancellationToken = default);
}
```

## Configuration

### Service Registration

```csharp
services.AddDaprJobScheduler<BackgroundJobInfo, BackgroundJobRepository>(options =>
{
    options.Handlers.Register<SendEmailJobHandler>();
    options.Handlers.Register<GenerateReportJobHandler>();
    options.Handlers.Register<CleanupExpiredDataJobHandler>();
});
```

### Application Configuration

```csharp
var app = builder.Build();

// Map Dapr job handler endpoint
app.UseDaprScheduledJobHandler();

app.Run();
```

## Entity Setup

### BackgroundJobInfo Entity

```csharp
public class BackgroundJobInfo : Entity<Guid>
{
    public string JobName { get; set; }
    public DateTime CreatedAt { get; set; }
}

// Repository
public class BackgroundJobRepository : EfCoreRepository<MyDbContext, BackgroundJobInfo, Guid>
{
    public BackgroundJobRepository(MyDbContext dbContext, IServiceProvider serviceProvider)
        : base(dbContext, serviceProvider)
    {
    }
}
```

## Usage Examples

### Defining a Job Handler

```csharp
public class SendEmailJobArgs
{
    public string To { get; set; }
    public string Subject { get; set; }
    public string Body { get; set; }
}

public class SendEmailJobHandler : IBackgroundJobHandler<SendEmailJobArgs>
{
    private readonly IEmailService _emailService;
    private readonly ILogger<SendEmailJobHandler> _logger;
    
    public SendEmailJobHandler(
        IEmailService emailService,
        ILogger<SendEmailJobHandler> logger)
    {
        _emailService = emailService;
        _logger = logger;
    }
    
    public async Task ExecuteAsync(SendEmailJobArgs args, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Sending email to {To}", args.To);
        
        await _emailService.SendAsync(args.To, args.Subject, args.Body, cancellationToken);
        
        _logger.LogInformation("Email sent successfully to {To}", args.To);
    }
}
```

### Scheduling a Job

```csharp
public class OrderService
{
    private readonly IBackgroundJobService _jobService;
    
    public async Task PlaceOrderAsync(PlaceOrderDto dto)
    {
        // Create order
        var order = new Order(dto);
        await _orderRepository.InsertAsync(order);
        
        // Schedule confirmation email
        await _jobService.EnqueueAsync<
            DaprBackgroundJobOptions,
            SendEmailJobHandler,
            SendEmailJobArgs>(
                new DaprBackgroundJobOptions
                {
                    Schedule = "@once", // Execute once immediately
                    JobPayload = new SendEmailJobArgs
                    {
                        To = dto.CustomerEmail,
                        Subject = "Order Confirmation",
                        Body = $"Your order {order.OrderNumber} has been placed."
                    }
                });
    }
}
```

### Recurring Jobs

```csharp
// Daily report generation
await _jobService.EnqueueAsync<
    DaprBackgroundJobOptions,
    GenerateReportJobHandler,
    GenerateReportJobArgs>(
        new DaprBackgroundJobOptions
        {
            Schedule = "@daily", // Every day at midnight
            JobPayload = new GenerateReportJobArgs
            {
                ReportType = "DailySales"
            }
        });

// Custom cron expression - every hour
await _jobService.EnqueueAsync<
    DaprBackgroundJobOptions,
    CleanupJobHandler,
    CleanupJobArgs>(
        new DaprBackgroundJobOptions
        {
            Schedule = "0 * * * *", // Cron: every hour
            Repeats = 0, // Infinite repeats
            JobPayload = new CleanupJobArgs
            {
                OlderThanDays = 30
            }
        });
```

### Job with Options

```csharp
public class DaprBackgroundJobOptions
{
    public string Schedule { get; set; } // Cron expression or @once, @daily, etc.
    public object? JobPayload { get; set; }
    public DateTimeOffset? StartingFrom { get; set; }
    public int? Repeats { get; set; } // null or 0 = infinite
    public TimeSpan? Ttl { get; set; } // Time to live
}

// Usage
await _jobService.EnqueueAsync<DaprBackgroundJobOptions, MyJobHandler, MyJobArgs>(
    new DaprBackgroundJobOptions
    {
        Schedule = "@daily",
        StartingFrom = DateTimeOffset.UtcNow.AddDays(1), // Start tomorrow
        Repeats = 30, // Run for 30 days
        JobPayload = new MyJobArgs { /* ... */ }
    });
```

### Deleting a Job

```csharp
public async Task CancelScheduledEmailAsync(Guid jobId)
{
    var deleted = await _jobService.DeleteAsync(jobId);
    if (deleted)
    {
        _logger.LogInformation("Job {JobId} cancelled successfully", jobId);
    }
}
```

## Complex Job Examples

### Report Generation

```csharp
public class GenerateReportJobArgs
{
    public string ReportType { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
}

public class GenerateReportJobHandler : IBackgroundJobHandler<GenerateReportJobArgs>
{
    private readonly IReportService _reportService;
    private readonly IStorageService _storageService;
    
    [UnitOfWork]
    public async Task ExecuteAsync(GenerateReportJobArgs args, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Generating {ReportType} report", args.ReportType);
        
        // Generate report
        var report = await _reportService.GenerateAsync(
            args.ReportType,
            args.StartDate,
            args.EndDate,
            cancellationToken);
        
        // Store report
        var reportUrl = await _storageService.StoreAsync(report, cancellationToken);
        
        // Notify completion
        await _notificationService.NotifyReportReadyAsync(reportUrl, cancellationToken);
        
        _logger.LogInformation("Report generated and stored at {ReportUrl}", reportUrl);
    }
}
```

### Data Cleanup

```csharp
public class CleanupExpiredDataJobArgs
{
    public int OlderThanDays { get; set; }
}

public class CleanupExpiredDataJobHandler : IBackgroundJobHandler<CleanupExpiredDataJobArgs>
{
    private readonly IRepository<TemporaryData> _repository;
    
    [UnitOfWork]
    public async Task ExecuteAsync(CleanupExpiredDataJobArgs args, CancellationToken cancellationToken)
    {
        var cutoffDate = DateTime.UtcNow.AddDays(-args.OlderThanDays);
        
        _logger.LogInformation("Cleaning up data older than {CutoffDate}", cutoffDate);
        
        // Delete expired data
        await _repository.DeleteDirectAsync(
            d => d.CreatedAt < cutoffDate,
            saveChanges: true,
            cancellationToken: cancellationToken);
        
        _logger.LogInformation("Cleanup completed");
    }
}
```

## Cron Expressions

### Common Patterns

```csharp
// Pre-defined
"@once"     // Execute once immediately
"@daily"    // Every day at midnight
"@hourly"   // Every hour
"@monthly"  // First day of month
"@yearly"   // January 1st

// Custom cron expressions
"0 * * * *"        // Every hour
"0 0 * * *"        // Every day at midnight
"0 9 * * MON-FRI"  // 9 AM on weekdays
"0 0 1 * *"        // First day of each month
"*/15 * * * *"     // Every 15 minutes
```

## Best Practices

### 1. Make Handlers Idempotent

```csharp
// ✅ Good: Idempotent handler
public class ProcessPaymentJobHandler : IBackgroundJobHandler<ProcessPaymentJobArgs>
{
    public async Task ExecuteAsync(ProcessPaymentJobArgs args, CancellationToken ct)
    {
        var payment = await _repository.GetAsync(args.PaymentId, cancellationToken: ct);
        
        // Check if already processed
        if (payment.IsProcessed)
        {
            _logger.LogInformation("Payment {PaymentId} already processed", args.PaymentId);
            return;
        }
        
        // Process
        await ProcessPaymentAsync(payment, ct);
        payment.MarkAsProcessed();
        await _repository.UpdateAsync(payment, cancellationToken: ct);
    }
}
```

### 2. Handle Errors Gracefully

```csharp
public async Task ExecuteAsync(MyJobArgs args, CancellationToken ct)
{
    try
    {
        await ProcessAsync(args, ct);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Job execution failed for {JobType}", GetType().Name);
        
        // Store error for monitoring
        await _errorTracker.RecordAsync(new JobError
        {
            JobType = GetType().Name,
            Error = ex.Message,
            Args = JsonSerializer.Serialize(args)
        }, ct);
        
        throw; // Re-throw for Dapr to handle retry
    }
}
```

### 3. Use Appropriate Scheduling

```csharp
// ✅ Good: Match schedule to requirement
// High-frequency data aggregation
Schedule = "*/5 * * * *" // Every 5 minutes

// Daily cleanup
Schedule = "@daily"

// Business hours only
Schedule = "0 9-17 * * MON-FRI" // 9 AM - 5 PM weekdays
```

### 4. Keep Jobs Focused

```csharp
// ✅ Good: Single responsibility
public class SendOrderConfirmationJobHandler : IBackgroundJobHandler<SendOrderConfirmationJobArgs>
{
    public async Task ExecuteAsync(SendOrderConfirmationJobArgs args, CancellationToken ct)
    {
        await _emailService.SendOrderConfirmationAsync(args.OrderId, ct);
    }
}

// ❌ Bad: Multiple responsibilities
public class ProcessOrderJobHandler : IBackgroundJobHandler<ProcessOrderJobArgs>
{
    public async Task ExecuteAsync(ProcessOrderJobArgs args, CancellationToken ct)
    {
        await ProcessPaymentAsync(args.OrderId, ct);
        await UpdateInventoryAsync(args.OrderId, ct);
        await SendConfirmationAsync(args.OrderId, ct);
        await UpdateAnalyticsAsync(args.OrderId, ct);
        // Too many responsibilities
    }
}
```

## Testing

```csharp
public class SendEmailJobHandlerTests
{
    private readonly Mock<IEmailService> _mockEmailService;
    private readonly SendEmailJobHandler _handler;
    
    [Fact]
    public async Task Execute_ShouldSendEmail()
    {
        // Arrange
        var args = new SendEmailJobArgs
        {
            To = "test@example.com",
            Subject = "Test",
            Body = "Test body"
        };
        
        // Act
        await _handler.ExecuteAsync(args, default);
        
        // Assert
        _mockEmailService.Verify(
            e => e.SendAsync(
                "test@example.com",
                "Test",
                "Test body",
                default),
            Times.Once);
    }
}
```

## Related Features

- **[Distributed Lock](../distributed-lock/README.md)** - Coordinate job execution
- **[Unit of Work](../unit-of-work/README.md)** - Transaction management

