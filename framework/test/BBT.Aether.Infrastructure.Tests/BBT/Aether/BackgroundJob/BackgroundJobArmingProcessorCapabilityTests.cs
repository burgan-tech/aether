using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.BackgroundJob.Processing;
using BBT.Aether.Clock;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.Repositories;
using BBT.Aether.MultiSchema;
using BBT.Aether.Uow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Aether.BackgroundJob;

public sealed class BackgroundJobArmingProcessorCapabilityTests
{
    [Fact]
    public async Task Legacy_store_is_declined_before_claim_or_scheduler_side_effect()
    {
        var jobStore = Substitute.For<IJobStore>();
        var leaseStore = Substitute.For<IJobArmingLeaseStore>();
        var scheduler = Substitute.For<IJobScheduler>();
        var uowManager = Substitute.For<IUnitOfWorkManager>();
        var currentSchema = Substitute.For<ICurrentSchema>();
        var clock = Substitute.For<IClock>();
        currentSchema.Change(Arg.Any<string>()).Returns(Substitute.For<IDisposable>());
        clock.UtcNow.Returns(DateTime.UtcNow);

        var job = new BackgroundJobInfo(Guid.NewGuid(), "handler", "legacy-store-job")
        {
            ExpressionValue = "@every 1m",
            Payload = JsonDocument.Parse("{}").RootElement.Clone(),
            Status = BackgroundJobStatus.Pending,
            Kind = JobKind.Recurring
        };
        leaseStore.ClaimBatchAsync(default, default!, default, default)
            .ReturnsForAnyArgs(new List<BackgroundJobArmingClaim>
            {
                new(job, BackgroundJobStatus.Pending, Guid.NewGuid())
            });
        jobStore.GetStaleRunningAsync(default, default, default)
            .ReturnsForAnyArgs(Array.Empty<BackgroundJobInfo>());

        var services = new ServiceCollection();
        services.AddSingleton(jobStore);
        services.AddSingleton(leaseStore);
        services.AddSingleton(scheduler);
        services.AddSingleton(uowManager);
        services.AddSingleton(currentSchema);
        await using var provider = services.BuildServiceProvider();

        var processor = new BackgroundJobArmingProcessor(
            provider.GetRequiredService<IServiceScopeFactory>(),
            clock,
            new BackgroundJobOptions { Schema = "legacy", ArmingBatchSize = 1 },
            NullLogger<BackgroundJobArmingProcessor>.Instance);

        await processor.RunAsync();

        await leaseStore.DidNotReceiveWithAnyArgs()
            .ClaimBatchAsync(default, default!, default, default);
        await scheduler.DidNotReceiveWithAnyArgs()
            .ScheduleAsync(default!, default!, default!, default, default, default);
        await scheduler.DidNotReceiveWithAnyArgs()
            .ScheduleOneShotAsync(default!, default!, default, default, default, default);
    }
}
