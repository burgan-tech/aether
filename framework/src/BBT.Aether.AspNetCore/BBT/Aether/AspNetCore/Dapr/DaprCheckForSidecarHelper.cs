using System;
using System.Threading;
using System.Threading.Tasks;
using Dapr.Client;

namespace BBT.Aether.AspNetCore.Dapr;

public static class DaprCheckForSidecarHelper
{
    public async static Task CheckAsync(DaprClient daprClient, int delayTimeInSeconds = 20000)
    {
        using var tokenSource = new CancellationTokenSource(delayTimeInSeconds);
        try
        {
            await daprClient.WaitForSidecarAsync(tokenSource.Token);
        }
        catch (Exception ex)
        {
            throw new DaprCheckSidecarException(ex.Message, ex);
        }
    }
}