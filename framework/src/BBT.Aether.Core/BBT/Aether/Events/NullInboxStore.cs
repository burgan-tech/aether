using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>
/// Null implementation of the inbox store that does nothing.
/// Used when inbox pattern is not configured.
/// </summary>
public class NullInboxStore : IInboxStore
{
    public Task<bool> HasProcessedAsync(string eventId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }

    public Task MarkProcessedAsync(CloudEventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}

