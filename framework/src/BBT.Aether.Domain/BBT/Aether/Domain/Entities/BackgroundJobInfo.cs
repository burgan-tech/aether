using System;
using System.Text.Json;
using BBT.Aether.Domain.Entities.Auditing;

namespace BBT.Aether.Domain.Entities;

/// <summary>
/// Represents comprehensive information about a background job including its configuration,
/// payload, metadata, and execution state.
/// </summary>
public class BackgroundJobInfo : FullAuditedEntity<Guid>, IHasExtraProperties
{
    private BackgroundJobInfo()
    {
        
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BackgroundJobInfo"/> class.
    /// </summary>
    /// <param name="id">The unique identifier for the background job.</param>
    /// <param name="handlerName">The name of the handler type.</param>
    /// <param name="jobName">The unique job name for this specific job instance.</param>
    public BackgroundJobInfo(Guid id, string handlerName, string jobName) : base(id)
    {
        HandlerName = handlerName;
        JobName = jobName;
        ExtraProperties = new ExtraPropertyDictionary();
    }

    /// <summary>
    /// Gets or sets the name of the handler type. This identifier is used to route
    /// the job to the appropriate handler during execution (e.g., "SendEmail", "GenerateReport").
    /// </summary>
    public string HandlerName { get; private set; } = default!;
    
    /// <summary>
    /// Gets or sets the unique job name for this specific job instance.
    /// This name is used by the external scheduler for job tracking, routing, and cancellation (e.g., "send-email-order-123").
    /// </summary>
    public string JobName { get; private set; } = default!;

    /// <summary>
    /// Gets or sets the schedule expression value that determines when the job should be executed.
    /// This typically contains cron expressions or time-based scheduling information.
    /// </summary>
    public string ExpressionValue { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the job payload containing the data to be processed by the job handler.
    /// Stored as JsonElement for flexible, schema-less storage.
    /// </summary>
    public JsonElement Payload { get; set; }

    /// <summary>
    /// Gets or sets the current status of the background job.
    /// </summary>
    public BackgroundJobStatus Status { get; set; } = BackgroundJobStatus.Scheduled;

    /// <summary>
    /// Gets or sets when the job was handled (completed or failed).
    /// </summary>
    public DateTime? HandledTime { get; set; }

    /// <summary>
    /// Gets or sets the number of retry attempts for this job.
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Gets or sets the last error message if the job failed.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Gets or sets additional metadata associated with the job.
    /// Common metadata includes domain, flow name, and instance ID information.
    /// </summary>
    public ExtraPropertyDictionary ExtraProperties { get; set; } = new ExtraPropertyDictionary();
}