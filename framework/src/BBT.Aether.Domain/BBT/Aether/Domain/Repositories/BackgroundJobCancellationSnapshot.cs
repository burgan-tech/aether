using BBT.Aether.Domain.Entities;

namespace BBT.Aether.Domain.Repositories;

/// <summary>
/// Current persisted fields needed to classify and clean up a waiting-job cancellation.
/// This read model is returned from a fresh, non-tracking store query.
/// </summary>
public sealed record BackgroundJobCancellationSnapshot(
    string HandlerName,
    string JobName,
    BackgroundJobStatus Status);
