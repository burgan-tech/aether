using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

public interface IOutboxStore
{
    Task StoreAsync(Type eventType, object eventData, CancellationToken cancellationToken = default);
}
