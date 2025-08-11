namespace BBT.Aether.BackgroundJob.Dapr;

public class DaprJobSchedulerOptions
{
    public DaprJobSchedulerHandlerList Handlers { get; } = new();
}

