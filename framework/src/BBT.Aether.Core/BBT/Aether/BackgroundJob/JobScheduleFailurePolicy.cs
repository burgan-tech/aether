using System;

namespace BBT.Aether.BackgroundJob;

public sealed class JobScheduleFailurePolicy
{
    public FailurePolicyType PolicyType { get; private init; }
    public TimeSpan? Interval { get; private init; }
    public uint? MaxRetries { get; private init; }

    public static JobScheduleFailurePolicy Drop() =>
        new() { PolicyType = FailurePolicyType.Drop };

    public static JobScheduleFailurePolicy Constant(TimeSpan interval, uint? maxRetries = null) =>
        new() { PolicyType = FailurePolicyType.Constant, Interval = interval, MaxRetries = maxRetries };
}

public enum FailurePolicyType { Drop, Constant }
