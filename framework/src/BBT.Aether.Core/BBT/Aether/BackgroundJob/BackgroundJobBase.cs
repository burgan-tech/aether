using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.BackgroundJob;

public abstract class BackgroundJobBase<TArgs> 
{
    protected readonly ILogger Logger;
    protected BackgroundJobBase(ILogger logger)
    {
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public abstract Task HandleAsync(TArgs jobPayload, CancellationToken cancellationToken);
}
