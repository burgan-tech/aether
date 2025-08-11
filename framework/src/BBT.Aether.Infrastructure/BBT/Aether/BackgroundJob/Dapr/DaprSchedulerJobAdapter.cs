using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.BackgroundJob.Dapr;

public class DaprSchedulerJobAdapter<TArgs> : IJobExecute<TArgs>
{
    private readonly IBackgroundJobHandler<TArgs> _handler;
    public DaprSchedulerJobAdapter(IBackgroundJobHandler<TArgs> handler)
    {
        _handler = handler;
    }

    public async Task Execute(TArgs args, CancellationToken cancellationToken)
    {
        await _handler.HandleAsync(args, cancellationToken);
    }
}