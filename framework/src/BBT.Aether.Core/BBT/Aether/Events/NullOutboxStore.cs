using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>Null implementation of the outbox store. Used when outbox is not configured.</summary>
public class NullOutboxStore : IOutboxStore
{
    public Task StoreAsync(CloudEventEnvelope envelope, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
