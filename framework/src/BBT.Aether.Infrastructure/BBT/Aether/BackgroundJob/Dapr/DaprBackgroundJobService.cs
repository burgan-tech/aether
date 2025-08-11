using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Dapr;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.Repositories;
using Dapr.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Aether.BackgroundJob.Dapr;

public class DaprBackgroundJobService<TJobInfo> : IBackgroundJobService where TJobInfo : BackgroundJobInfo
{
    private readonly DaprJobsClient _daprJobsClient;
    private readonly ILogger<DaprBackgroundJobService<TJobInfo>> _logger;
    private readonly IRepository<BackgroundJobInfo> _jobInfoRepository;
    private readonly IOptions<DaprJobSchedulerOptions> _options;

    public DaprBackgroundJobService(DaprJobsClient daprJobsClient, ILogger<DaprBackgroundJobService<TJobInfo>> logger, IOptions<DaprJobSchedulerOptions> options, IRepository<BackgroundJobInfo> jobInfoRepository)
    {
        _daprJobsClient = daprJobsClient ?? throw new ArgumentNullException(nameof(daprJobsClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _jobInfoRepository = jobInfoRepository;
    }

    public virtual async Task<bool> DeleteAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var jobInfo = await _jobInfoRepository.GetAsync(k => k.Id == jobId, cancellationToken: cancellationToken);
        if (jobInfo is null)
            return false;

        await _daprJobsClient.DeleteJobAsync(jobInfo.JobName, cancellationToken: cancellationToken);

        await _jobInfoRepository.DeleteAsync(jobInfo, cancellationToken: cancellationToken);

        return true;
    }

    public virtual async Task<Guid> EnqueueAsync<TOpts, TJob, TArgs>(TOpts args, CancellationToken cancellationToken = default) where TJob : IBackgroundJobHandler<TArgs>
    {
        var daprJobOptions = args as DaprBackgroundJobOptions;

        if (daprJobOptions == null)
            throw new ArgumentNullException(nameof(daprJobOptions));

        var jobName = typeof(TJob).Name;
        var schedule = daprJobOptions.Schedule;

        if (!_options.Value.Handlers.JobHandlers.Any(k => k.JobName == jobName))
        {
            throw new InvalidOperationException($"No registered handler implementation found for job '{jobName}'.");
        }

        _logger.LogInformation("Scheduling job {JobName}", jobName);

        var backgroundJobInfo = await _jobInfoRepository.InsertAsync(new BackgroundJobInfo
        {
            JobName = jobName
        },
        saveChanges: false,
        cancellationToken: cancellationToken);

        await _daprJobsClient.ScheduleJobAsync(jobName, schedule, daprJobOptions?.JobPayload, daprJobOptions?.StartingFrom, repeats: daprJobOptions?.Repeats, ttl: daprJobOptions?.Ttl, cancellationToken: cancellationToken);

        await _jobInfoRepository.SaveChangesAsync(cancellationToken);

        return backgroundJobInfo.Id;
    }
}
