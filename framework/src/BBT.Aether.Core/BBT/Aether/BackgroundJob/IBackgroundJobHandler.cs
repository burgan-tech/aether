using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.BackgroundJob;

public interface IBackgroundJobHandler<in TArgs>
{
    Task HandleAsync(TArgs args, CancellationToken cancellationToken);
}
