# Background Jobs

## Overview

Scheduled job execution framework with pluggable schedulers (default: Dapr Jobs). Provides type-safe handlers, job persistence, status tracking, and idempotent execution.

## Quick Start

### DbContext Setup

```csharp
public class MyDbContext : AetherDbContext<MyDbContext>, IHasEfCoreBackgroundJobs
{
    public DbSet<BackgroundJobInfo> BackgroundJobs { get; set; } = null!;
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ConfigureBackgroundJob();
    }
}
```

### Service Registration

```csharp
services.AddAetherBackgroundJob<MyDbContext>(options =>
{
    options.AddHandler<SendEmailJobHandler>("SendEmail");
    options.AddHandler<GenerateReportJobHandler>("GenerateReport");
})
.AddDaprJobScheduler();
```

### Application Setup

```csharp
var app = builder.Build();
app.UseDaprScheduledJobHandler(); // Map Dapr job endpoint
app.Run();
```

## Defining Job Handlers

```csharp
// Job arguments
public class SendEmailJobArgs
{
    public required string To { get; set; }
    public required string Subject { get; set; }
    public required string Body { get; set; }
}

// Handler implementation
public class SendEmailJobHandler : IBackgroundJobHandler<SendEmailJobArgs>
{
    private readonly IEmailService _emailService;
    
    public SendEmailJobHandler(IEmailService emailService)
    {
        _emailService = emailService;
    }
    
    public async Task HandleAsync(SendEmailJobArgs args, CancellationToken ct)
    {
        await _emailService.SendAsync(args.To, args.Subject, args.Body, ct);
    }
}
```

## Scheduling Jobs

### One-Time Job

```csharp
var jobId = await _jobService.EnqueueAsync(
    handlerName: "SendEmail",
    jobName: $"send-email-{orderId}",
    payload: new SendEmailJobArgs
    {
        To = "customer@example.com",
        Subject = "Order Confirmation",
        Body = "Your order has been placed."
    },
    schedule: "@once");
```

### Recurring Jobs

```csharp
// Daily report
await _jobService.EnqueueAsync(
    handlerName: "GenerateReport",
    jobName: "daily-sales-report",
    payload: new GenerateReportJobArgs { ReportType = "DailySales" },
    schedule: "@daily");

// Custom cron - every 15 minutes
await _jobService.EnqueueAsync(
    handlerName: "CleanupData",
    jobName: "temp-cleanup",
    payload: new CleanupJobArgs { OlderThanDays = 30 },
    schedule: "*/15 * * * *");
```

### With Metadata

```csharp
await _jobService.EnqueueAsync(
    handlerName: "SendEmail",
    jobName: $"email-{orderId}",
    payload: emailArgs,
    schedule: "@once",
    metadata: new Dictionary<string, object>
    {
        ["Domain"] = "Orders",
        ["OrderId"] = orderId
    });
```

## Job Management

```csharp
// Update schedule
await _jobService.UpdateAsync(jobId, "@weekly");

// Delete job
await _jobService.DeleteAsync(jobId);

// Query jobs
var activeJobs = await _jobStore.GetActiveAsync();
var jobsByHandler = await _jobStore.GetByHandlerNameAsync("SendEmail");
```

## Schedule Expressions

### Pre-defined (Dapr)

```csharp
"@once"    // Execute immediately once
"@daily"   // Every day at midnight
"@hourly"  // Every hour
"@monthly" // First day of month
```

### Cron Expressions

```csharp
"0 * * * *"        // Every hour
"0 9 * * MON-FRI"  // 9 AM weekdays
"*/15 * * * *"     // Every 15 minutes
"0 0 1 * *"        // First day of month
```

## Job Status Lifecycle

```
Scheduled → Running → Completed
                   → Failed
                   → Cancelled
```

## Best Practices

1. **Make handlers idempotent** - Jobs may be retried on failure
2. **Use unique job names** - Include entity ID: `send-email-order-{orderId}`
3. **Keep handlers focused** - Single responsibility per handler
4. **Handle errors gracefully** - Log and re-throw for framework retry handling
5. **Use metadata for context** - Add domain, entity ID for troubleshooting

## Idempotent Handler Example

```csharp
public class ProcessPaymentJobHandler : IBackgroundJobHandler<ProcessPaymentJobArgs>
{
    public async Task HandleAsync(ProcessPaymentJobArgs args, CancellationToken ct)
    {
        var payment = await _repository.GetAsync(args.PaymentId, ct);
        
        // Check if already processed
        if (payment.Status == PaymentStatus.Completed)
        {
            _logger.LogInformation("Payment already processed");
            return;
        }
        
        await ProcessPaymentLogicAsync(payment, ct);
        payment.MarkAsCompleted();
        await _repository.UpdateAsync(payment, ct);
    }
}
```

## Related Features

- [Unit of Work](../unit-of-work/README.md) - Transaction management for handlers
- [Distributed Lock](../distributed-lock/README.md) - Coordinate job execution
