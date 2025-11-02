using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

public class NullOutboxStore: IOutboxStore
{
    public Task StoreAsync(Type eventType, object eventData, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}