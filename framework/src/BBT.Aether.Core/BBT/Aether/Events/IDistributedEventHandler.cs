using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

public interface IDistributedEventHandler<TEvent>
{
    Task HandleAsync(CloudEventEnvelope<TEvent> envelope, CancellationToken cancellationToken);
}
