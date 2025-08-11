using System;

namespace BBT.Aether.BackgroundJob.Dapr;

public class JobHandlerInfo
{
    public JobHandlerInfo(Type jobHandler)
    {
        JobHandler = jobHandler ?? throw new ArgumentNullException(nameof(jobHandler));
        JobName = jobHandler.Name;
    }

    /// <summary>
    /// Gets the job handler type.
    /// </summary>
    public Type JobHandler { get; }

    /// <summary>
    /// Gets the job name, which is the name of the job handler type.
    /// </summary>
    public string JobName { get; }
}