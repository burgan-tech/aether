using BBT.Aether.Domain.Entities;

namespace BBT.Aether.Domain.Repositories;

/// <summary>
/// Current persisted fields needed to classify and clean up a waiting-job cancellation.
/// This read model is returned from a fresh, non-tracking store query.
/// </summary>
public sealed record BackgroundJobCancellationSnapshot(
    string HandlerName,
    string JobName,
    BackgroundJobStatus Status)
{
    /// <summary>
    /// Current arming lease token, when present. Used to distinguish a terminal winner from a newer
    /// arming lease without changing the snapshot's existing constructor/deconstruction contract.
    /// </summary>
    public System.Guid? ArmingToken { get; init; }
}
