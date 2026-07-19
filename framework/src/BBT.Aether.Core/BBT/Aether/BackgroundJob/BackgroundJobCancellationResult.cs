namespace BBT.Aether.BackgroundJob;

public enum BackgroundJobCancellationResult
{
    Cancelled,
    SkippedRunning,
    AlreadyTerminal,
    NotFound
}
