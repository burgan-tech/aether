using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.BackgroundJob;

public interface IJobExecute<TArgs>
{
    Task Execute(TArgs args, CancellationToken cancellationToken);
}
