using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.BackgroundJob;
using BBT.Aether.BackgroundJob.Dapr;
using Dapr.Jobs.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherDaprJobSchedulerAppExtensions
{
    public static WebApplication UseDaprScheduledJobHandler(this WebApplication app)
    {
        app.MapDaprScheduledJobHandler(async (string jobName, ReadOnlyMemory<byte> jobPayload, CancellationToken cancellationToken) =>
              {
                  using var scope = app.Services.CreateScope();
                  var serviceProvider = scope.ServiceProvider;

                  var options = serviceProvider.GetRequiredService<IOptions<DaprJobSchedulerOptions>>().Value;

                  var handlerInfo = options.Handlers.JobHandlers.FirstOrDefault(h => h.JobName == jobName);
                  if (handlerInfo == null)
                  {
                      throw new InvalidOperationException($"No registered handler implementation found for job '{jobName}'.");
                  }

                  var handlerType = handlerInfo.JobHandler;

                  var interfaceType = handlerType.GetInterfaces()
                            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IBackgroundJobHandler<>));

                  if (interfaceType != null)
                  {
                      var argType = interfaceType.GetGenericArguments().First();

                      var adapterType = typeof(IJobExecute<>).MakeGenericType(argType);

                      var jobHandler = serviceProvider.GetRequiredService(adapterType);

                      var payload = System.Text.Json.JsonSerializer.Deserialize(jobPayload.Span, argType);
                      if (payload == null)
                      {
                          throw new InvalidOperationException($"Failed to deserialize payload for job '{jobName}'.");
                      }

                      var executeMethod = jobHandler.GetType().GetMethod(nameof(DaprSchedulerJobAdapter<object>.Execute));
                      if (executeMethod == null)
                      {
                          throw new InvalidOperationException($"Method '{nameof(DaprSchedulerJobAdapter<object>.Execute)}' not found on '{jobHandler.GetType().Name}'.");
                      }

                      var task = (Task?)executeMethod.Invoke(jobHandler, [payload, CancellationToken.None]);
                      if (task == null)
                      {
                          throw new InvalidOperationException($"Failed to invoke '{nameof(DaprSchedulerJobAdapter<object>.Execute)}' on '{jobHandler.GetType().Name}'.");
                      }

                      await task;
                  }
                  else
                  {
                      throw new InvalidOperationException($"No registered handler implementation found for job '{jobName}'.");
                  }
              });

        return app;
    }

}