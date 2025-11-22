# Background Jobs

## Overview

Aether's Background Job system provides a robust, extensible scheduled job execution framework with pluggable scheduler support. The default implementation uses Dapr Jobs, but the architecture supports other schedulers like Quartz, Hangfire, etc.

## Key Features

- **Pluggable Schedulers** - Abstract scheduler interface (default: Dapr Jobs)
- **Job Persistence** - Full job lifecycle tracking in database
- **Type-Safe Handlers** - Strongly-typed, dependency-injected job handlers
- **Status Tracking** - Scheduled → Running → Completed/Failed/Cancelled
- **Idempotency** - Prevents duplicate execution on job re-delivery
- **Unit of Work Integration** - Transactional job status updates
- **CRUD Operations** - Create, update, delete, and query scheduled jobs
- **No Runtime Reflection** - Handler invocation uses startup-time type closure
- **Clean Architecture** - Domain, Core, and Infrastructure separation

## Terminology

- **Handler Name**: The type of job handler (e.g., "SendEmail", "GenerateReport") - used for internal routing to the correct handler
- **Job Name**: Unique identifier for a specific job instance in the external scheduler (e.g., "send-email-order-123")

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                      Application Layer                       │
│                 (IBackgroundJobService)                      │
└────────────────────────┬────────────────────────────────────┘
                         │
         ┌───────────────┴───────────────┐
         │                               │
┌────────▼────────┐            ┌────────▼────────┐
│  IJobScheduler  │            │   IJobStore     │
│  (Dapr/Quartz)  │            │  (Persistence)  │
└─────────────────┘            └─────────────────┘
         │                               │
         │ Trigger                       │ Status
         ▼                               ▼
┌─────────────────┐            ┌─────────────────┐
│IJobExecution    │───────────▶│  IJobDispatcher │
│Bridge (Dapr)    │  Delegate  │   (Routing)     │
└─────────────────┘            └────────┬────────┘
                                        │
                                        ▼
                               ┌─────────────────┐
                               │IBackgroundJob   │
                               │Invoker<TArgs>   │
                               └────────┬────────┘
                                        │
                                        ▼
                               ┌─────────────────┐
                               │IBackgroundJob   │
                               │Handler<TArgs>   │
                               └─────────────────┘
```

## Core Interfaces

### IBackgroundJobService

High-level API for job management (create, update, delete).

```csharp
public interface IBackgroundJobService
{
    Task<Guid> EnqueueAsync<TPayload>(
        string handlerName,     // "SendEmail", "GenerateReport"
        string jobName,         // "send-email-order-123"
        TPayload payload,
        string schedule,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default) where TPayload : class;
    
    Task UpdateAsync(Guid id, string newSchedule, CancellationToken cancellationToken = default);
    
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
```

### IBackgroundJobHandler<TArgs>

Contract for implementing job handlers.

```csharp
public interface IBackgroundJobHandler<in TArgs>
{
    Task HandleAsync(TArgs args, CancellationToken cancellationToken = default);
}
```

### IJobStore

Repository interface for job persistence.

```csharp
public interface IJobStore
{
    Task SaveAsync(BackgroundJobInfo jobInfo, CancellationToken cancellationToken = default);
    Task<BackgroundJobInfo?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<BackgroundJobInfo?> GetByJobNameAsync(string jobName, CancellationToken cancellationToken = default);
    Task<IEnumerable<BackgroundJobInfo>> GetByHandlerNameAsync(string handlerName, CancellationToken cancellationToken = default);
    Task<IEnumerable<BackgroundJobInfo>> GetActiveAsync(CancellationToken cancellationToken = default);
    Task UpdateStatusAsync(Guid id, BackgroundJobStatus status, DateTime? handledTime = null, string? error = null, CancellationToken cancellationToken = default);
}
```

### BackgroundJobInfo Entity

```csharp
public class BackgroundJobInfo : FullAuditedEntity<Guid>
{
    public required string HandlerName { get; set; }  // "SendEmail", "GenerateReport"
    public required string JobName { get; set; }      // "send-email-order-123"
    public string ExpressionValue { get; set; }       // Cron expression
    public JsonElement Payload { get; set; }
    public BackgroundJobStatus Status { get; set; }
    public DateTime? HandledTime { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public Dictionary<string, object> ExtraProperties { get; set; }
}
```

### BackgroundJobStatus Enum

```csharp
public enum BackgroundJobStatus
{
    Scheduled = 0,   // Job created but not started
    Running = 1,     // Currently executing
    Completed = 2,   // Successfully completed
    Failed = 3,      // Execution failed
    Cancelled = 4    // Job was cancelled
}
```

## Setup

### 1. DbContext Configuration

Implement `IHasEfCoreBackgroundJobs` interface:

```csharp
public class MyDbContext : AetherDbContext<MyDbContext>, IHasEfCoreBackgroundJobs
{
    public DbSet<BackgroundJobInfo> BackgroundJobs { get; set; } = null!;
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        // Configure BackgroundJobInfo entity
        modelBuilder.ConfigureBackgroundJob();
    }
}
```

### 2. Service Registration

Register background job services with your preferred scheduler:

```csharp
// Program.cs or Startup.cs
services.AddAetherBackgroundJob<MyDbContext>(options =>
{
    // Register job handlers with their handler names
    options.AddHandler<SendEmailJobHandler>("SendEmail");
    options.AddHandler<GenerateReportJobHandler>("GenerateReport");
    options.AddHandler<CleanupDataJobHandler>("CleanupData");
})
.AddDaprJobScheduler(); // Choose scheduler: Dapr, Quartz, etc.
```

### 3. Application Configuration

Map the scheduler endpoint (Dapr example):

```csharp
var app = builder.Build();

// Map Dapr job handler endpoint
app.UseDaprScheduledJobHandler();

app.Run();
```

## Usage

### Defining a Job Handler

```csharp
// 1. Define job arguments
public class SendEmailJobArgs
{
    public required string To { get; set; }
    public required string Subject { get; set; }
    public required string Body { get; set; }
}

// 2. Implement handler
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
    
    public async Task HandleAsync(SendEmailJobArgs args, CancellationToken cancellationToken)
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
        var jobName = $"send-email-{order.Id}";  // Unique job name for scheduler
        var entityId = await _jobService.EnqueueAsync(
            handlerName: "SendEmail",            // Which handler to use
            jobName: jobName,                    // Unique identifier for this job
            payload: new SendEmailJobArgs
            {
                To = dto.CustomerEmail,
                Subject = "Order Confirmation",
                Body = $"Your order {order.OrderNumber} has been placed."
            },
            schedule: "@once",                   // Execute once immediately
            metadata: new Dictionary<string, object>
            {
                ["OrderId"] = order.Id,
                ["Domain"] = "Orders"
            }
        );
        
        _logger.LogInformation("Scheduled email job with entity ID {EntityId}", entityId);
    }
}
```

### Recurring Jobs

```csharp
// Daily report generation
var reportJobId = await _jobService.EnqueueAsync(
    handlerName: "GenerateReport",
    jobName: "daily-sales-report",
    payload: new GenerateReportJobArgs
    {
        ReportType = "DailySales"
    },
    schedule: "@daily" // Every day at midnight
);

// Custom cron expression - every 15 minutes
var cleanupJobId = await _jobService.EnqueueAsync(
    handlerName: "CleanupData",
    jobName: "temp-data-cleanup",
    payload: new CleanupDataJobArgs
    {
        OlderThanDays = 30
    },
    schedule: "*/15 * * * *" // Every 15 minutes
);
```

### Updating a Job

```csharp
public async Task UpdateReportScheduleAsync(Guid jobEntityId)
{
    await _jobService.UpdateAsync(
        id: jobEntityId,
        newSchedule: "@weekly" // Change to weekly
    );
}
```

### Deleting a Job

```csharp
public async Task CancelScheduledJobAsync(Guid jobEntityId)
{
    var deleted = await _jobService.DeleteAsync(jobEntityId);
    if (deleted)
    {
        _logger.LogInformation("Job {JobEntityId} cancelled successfully", jobEntityId);
    }
    else
    {
        _logger.LogWarning("Job {JobEntityId} not found", jobEntityId);
    }
}
```

## Advanced Usage

### Querying Jobs

Use `IJobStore` for advanced queries:

```csharp
public class JobMonitoringService
{
    private readonly IJobStore _jobStore;
    
    public async Task<IEnumerable<BackgroundJobInfo>> GetActiveJobsAsync()
    {
        // Get all scheduled or running jobs
        return await _jobStore.GetActiveAsync();
    }
    
    public async Task<IEnumerable<BackgroundJobInfo>> GetJobsByHandlerAsync(string handlerName)
    {
        // Get all jobs of a specific handler type
        return await _jobStore.GetByHandlerNameAsync(handlerName);
    }
    
    public async Task<BackgroundJobInfo?> GetJobByNameAsync(string jobName)
    {
        // Get job by scheduler job name
        return await _jobStore.GetByJobNameAsync(jobName);
    }
}
```

### Complex Job Example: Report Generation

```csharp
public class GenerateReportJobArgs
{
    public required string ReportType { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
}

public class GenerateReportJobHandler : IBackgroundJobHandler<GenerateReportJobArgs>
{
    private readonly IReportService _reportService;
    private readonly IStorageService _storageService;
    private readonly INotificationService _notificationService;
    private readonly ILogger<GenerateReportJobHandler> _logger;
    
    [UnitOfWork] // Automatic transaction management
    public async Task HandleAsync(GenerateReportJobArgs args, CancellationToken cancellationToken)
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

### Data Cleanup Job

```csharp
public class CleanupDataJobArgs
{
    public int OlderThanDays { get; set; }
}

public class CleanupDataJobHandler : IBackgroundJobHandler<CleanupDataJobArgs>
{
    private readonly IRepository<TemporaryData> _repository;
    private readonly ILogger<CleanupDataJobHandler> _logger;
    
    [UnitOfWork]
    public async Task HandleAsync(CleanupDataJobArgs args, CancellationToken cancellationToken)
    {
        var cutoffDate = DateTime.UtcNow.AddDays(-args.OlderThanDays);
        
        _logger.LogInformation("Cleaning up data older than {CutoffDate}", cutoffDate);
        
        var count = await _repository.DeleteDirectAsync(
            d => d.CreatedAt < cutoffDate,
            saveChanges: true,
            cancellationToken: cancellationToken);
        
        _logger.LogInformation("Cleanup completed. Deleted {Count} records", count);
    }
}
```

## Cron Expressions

### Pre-defined Schedules (Dapr)

```csharp
"@once"     // Execute once immediately
"@daily"    // Every day at midnight
"@hourly"   // Every hour
"@monthly"  // First day of month
"@yearly"   // January 1st
```

### Custom Cron Expressions

```csharp
"0 * * * *"        // Every hour
"0 0 * * *"        // Every day at midnight
"0 9 * * MON-FRI"  // 9 AM on weekdays
"0 0 1 * *"        // First day of each month
"*/15 * * * *"     // Every 15 minutes
"0 */2 * * *"      // Every 2 hours
"0 0 * * SUN"      // Every Sunday at midnight
```

## Best Practices

### 1. Use Clear Handler Names

```csharp
// ✅ Good: Descriptive handler names
options.AddHandler<SendEmailJobHandler>("SendEmail");
options.AddHandler<GenerateReportJobHandler>("GenerateReport");
options.AddHandler<ProcessPaymentJobHandler>("ProcessPayment");

// ❌ Bad: Generic or unclear names
options.AddHandler<MyHandler>("Handler1");
options.AddHandler<JobProcessor>("Job");
```

### 2. Make Job Names Unique

```csharp
// ✅ Good: Unique job names that avoid collisions
var jobName = $"send-email-order-{orderId}-{DateTime.UtcNow.Ticks}";
var jobName = $"process-payment-{paymentId}";

// ❌ Bad: Generic job names that may collide
var jobName = "email-job";
var jobName = "process";
```

### 3. Make Handlers Idempotent

Jobs may be retried or re-delivered. Ensure handlers can safely execute multiple times:

```csharp
public class ProcessPaymentJobHandler : IBackgroundJobHandler<ProcessPaymentJobArgs>
{
    public async Task HandleAsync(ProcessPaymentJobArgs args, CancellationToken ct)
    {
        var payment = await _repository.GetAsync(args.PaymentId, cancellationToken: ct);
        
        // ✅ Check if already processed
        if (payment.Status == PaymentStatus.Completed)
        {
            _logger.LogInformation("Payment {PaymentId} already processed. Skipping.", args.PaymentId);
            return;
        }
        
        // Process payment
        await ProcessPaymentAsync(payment, ct);
        payment.MarkAsCompleted();
        await _repository.UpdateAsync(payment, cancellationToken: ct);
    }
}
```

### 4. Handle Errors Gracefully

```csharp
public async Task HandleAsync(MyJobArgs args, CancellationToken ct)
{
    try
    {
        await ProcessAsync(args, ct);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Job execution failed for handler {HandlerType}", GetType().Name);
        
        // Store error for monitoring/alerting
        await _errorTracker.RecordAsync(new JobError
        {
            HandlerType = GetType().Name,
            JobName = args.JobName,
            Error = ex.Message,
            Args = JsonSerializer.Serialize(args)
        }, ct);
        
        throw; // Re-throw for framework to handle retry and status update
    }
}
```

### 5. Use Appropriate Scheduling

```csharp
// ✅ Good: Match schedule to requirement

// High-frequency data aggregation
schedule: "*/5 * * * *" // Every 5 minutes

// Daily cleanup
schedule: "@daily"

// Business hours only
schedule: "0 9-17 * * MON-FRI" // 9 AM - 5 PM weekdays

// End of month processing
schedule: "0 0 28-31 * *" // Last days of month
```

### 6. Keep Jobs Focused (Single Responsibility)

```csharp
// ✅ Good: Single responsibility
public class SendOrderConfirmationJobHandler : IBackgroundJobHandler<SendOrderConfirmationJobArgs>
{
    public async Task HandleAsync(SendOrderConfirmationJobArgs args, CancellationToken ct)
    {
        await _emailService.SendOrderConfirmationAsync(args.OrderId, ct);
    }
}

// ❌ Bad: Multiple responsibilities
public class ProcessOrderJobHandler : IBackgroundJobHandler<ProcessOrderJobArgs>
{
    public async Task HandleAsync(ProcessOrderJobArgs args, CancellationToken ct)
    {
        // Too many responsibilities - split into separate jobs
        await ProcessPaymentAsync(args.OrderId, ct);
        await UpdateInventoryAsync(args.OrderId, ct);
        await SendConfirmationAsync(args.OrderId, ct);
        await UpdateAnalyticsAsync(args.OrderId, ct);
    }
}
```

### 7. Use Metadata for Context

```csharp
await _jobService.EnqueueAsync(
    handlerName: "SendEmail",
    jobName: $"email-{orderId}",
    payload: emailArgs,
    schedule: "@once",
    metadata: new Dictionary<string, object>
    {
        ["Domain"] = "Orders",
        ["OrderId"] = orderId,
        ["Environment"] = "Production",
        ["TriggeredBy"] = currentUser.Id
    }
);
```

## Testing

### Unit Testing Handlers

```csharp
public class SendEmailJobHandlerTests
{
    private readonly Mock<IEmailService> _mockEmailService;
    private readonly Mock<ILogger<SendEmailJobHandler>> _mockLogger;
    private readonly SendEmailJobHandler _handler;
    
    public SendEmailJobHandlerTests()
    {
        _mockEmailService = new Mock<IEmailService>();
        _mockLogger = new Mock<ILogger<SendEmailJobHandler>>();
        _handler = new SendEmailJobHandler(_mockEmailService.Object, _mockLogger.Object);
    }
    
    [Fact]
    public async Task HandleAsync_ShouldSendEmail()
    {
        // Arrange
        var args = new SendEmailJobArgs
        {
            To = "test@example.com",
            Subject = "Test",
            Body = "Test body"
        };
        
        // Act
        await _handler.HandleAsync(args, default);
        
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

### Integration Testing

```csharp
public class BackgroundJobServiceIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    
    [Fact]
    public async Task EnqueueAsync_ShouldPersistJob()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var jobService = scope.ServiceProvider.GetRequiredService<IBackgroundJobService>();
        var jobStore = scope.ServiceProvider.GetRequiredService<IJobStore>();
        
        var jobName = "test-job-" + Guid.NewGuid();
        
        // Act
        var entityId = await jobService.EnqueueAsync(
            "SendEmail",
            jobName,
            new SendEmailJobArgs { To = "test@example.com", Subject = "Test", Body = "Test" },
            "@once"
        );
        
        // Assert
        var job = await jobStore.GetAsync(entityId);
        Assert.NotNull(job);
        Assert.Equal("SendEmail", job.HandlerName);
        Assert.Equal(jobName, job.JobName);
        Assert.Equal(BackgroundJobStatus.Scheduled, job.Status);
    }
}
```

## Monitoring and Observability

### Query Job Status

```csharp
public class JobMonitoringDashboard
{
    private readonly IJobStore _jobStore;
    
    public async Task<JobStatistics> GetStatisticsAsync()
    {
        var allJobs = await _jobStore.GetActiveAsync();
        
        return new JobStatistics
        {
            TotalActive = allJobs.Count(),
            Scheduled = allJobs.Count(j => j.Status == BackgroundJobStatus.Scheduled),
            Running = allJobs.Count(j => j.Status == BackgroundJobStatus.Running),
            AverageRetryCount = allJobs.Average(j => j.RetryCount)
        };
    }
    
    public async Task<List<BackgroundJobInfo>> GetFailedJobsAsync()
    {
        var allJobs = await _jobStore.GetActiveAsync();
        return allJobs
            .Where(j => j.Status == BackgroundJobStatus.Failed)
            .OrderByDescending(j => j.HandledTime)
            .ToList();
    }
}
```

## Extending with Custom Schedulers

### Implementing a Custom Scheduler

```csharp
public class QuartzJobScheduler : IJobScheduler
{
    private readonly IScheduler _quartzScheduler;
    
    public async Task ScheduleAsync(
        string handlerName,
        string jobName,
        string schedule,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        // Implement Quartz scheduling logic
        var job = JobBuilder.Create<QuartzJobAdapter>()
            .WithIdentity(jobName, handlerName)
            .UsingJobData("payload", payload.ToArray())
            .Build();
        
        var trigger = TriggerBuilder.Create()
            .WithIdentity($"{jobName}-trigger", handlerName)
            .WithCronSchedule(schedule)
            .Build();
        
        await _quartzScheduler.ScheduleJob(job, trigger, cancellationToken);
    }
    
    // Implement UpdateScheduleAsync, DeleteAsync...
}

// Registration
services.AddAetherBackgroundJob<MyDbContext>(options => { })
        .AddScoped<IJobScheduler, QuartzJobScheduler>();
```

## Related Features

- **[Unit of Work](../unit-of-work/README.md)** - Transaction management for job handlers
- **[Distributed Lock](../distributed-lock/README.md)** - Coordinate job execution across instances
- **[Auditing](../auditing/README.md)** - Track who created/modified jobs

## Troubleshooting

### Jobs Not Executing

1. Verify handler registration:
```csharp
options.AddHandler<MyJobHandler>("MyHandlerName");
```

2. Check endpoint mapping:
```csharp
app.UseDaprScheduledJobHandler();
```

3. Verify job name is unique:
```csharp
var jobName = $"my-job-{Guid.NewGuid()}";
```

### Jobs Stuck in "Running" Status

Jobs may get stuck if the handler crashes without updating status. Implement a recovery mechanism:

```csharp
public class JobRecoveryService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var stuckJobs = await _jobStore.GetActiveAsync();
            var timeout = TimeSpan.FromMinutes(30);
            
            foreach (var job in stuckJobs.Where(j => 
                j.Status == BackgroundJobStatus.Running && 
                DateTime.UtcNow - j.CreatedAt > timeout))
            {
                _logger.LogWarning("Job {JobName} (Handler: {HandlerName}) stuck in Running status, marking as Failed", 
                    job.JobName, job.HandlerName);
                await _jobStore.UpdateStatusAsync(job.Id, BackgroundJobStatus.Failed, 
                    DateTime.UtcNow, "Job timeout");
            }
            
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
```
