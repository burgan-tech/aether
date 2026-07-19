using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.Repositories;
using Shouldly;
using Xunit;

namespace BBT.Aether.BackgroundJob;

public sealed class JobStoreApiCompatibilityTests
{
    [Fact]
    public void Arming_transition_keeps_legacy_contract_and_moves_safe_operations_to_capability()
    {
        var overloads = typeof(IJobStore).GetMethods()
            .Where(method => method.Name == nameof(IJobStore.TryTransitionFromArmingAsync))
            .ToArray();

        var legacy = overloads.SingleOrDefault(method =>
            method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(
            [typeof(Guid), typeof(Guid), typeof(BackgroundJobStatus), typeof(CancellationToken)]));
        var guarded = overloads.SingleOrDefault(method =>
            method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(
            [typeof(Guid), typeof(Guid), typeof(BackgroundJobStatus), typeof(BackgroundJobStatus),
                typeof(CancellationToken)]));

        legacy.ShouldNotBeNull("existing callers and implementers require the original four-parameter API");
        legacy!.IsAbstract.ShouldBeTrue("legacy implementers continue to provide only their original method");
        guarded.ShouldBeNull(
            "IJobStore must not offer a default bridge that can silently fall back to token-only finalization");

        foreach (var methodName in new[]
                 {
                     nameof(IJobArmingStore.TryAcquireTerminalArmingCompensationAsync),
                     nameof(IJobArmingStore.TryRenewArmingCompensationAsync),
                     nameof(IJobArmingStore.TryReleaseArmingCompensationAsync)
                 })
        {
            typeof(IJobStore).GetMethod(methodName).ShouldBeNull(
                $"{methodName} must be opt-in rather than silently defaulting on legacy stores");
        }

        var capability = typeof(IJobStore).Assembly.GetType(
            "BBT.Aether.Domain.Repositories.IJobArmingStore");
        capability.ShouldNotBeNull();
        capability!.GetMethods().Length.ShouldBe(4);
        capability.GetMethods().ShouldAllBe(method => method.IsAbstract);

        capability.IsInstanceOfType(new LegacyOnlyJobStore()).ShouldBeFalse(
            "an existing source-compatible IJobStore implementation must not silently acquire safe arming semantics");
    }

    private sealed class LegacyOnlyJobStore : IJobStore
    {
        public Task SaveAsync(BackgroundJobInfo jobInfo, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BackgroundJobInfo?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BackgroundJobCancellationSnapshot?> GetCancellationSnapshotAsync(
            Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<BackgroundJobInfo?> GetByJobNameAsync(
            string jobName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IEnumerable<BackgroundJobInfo>> GetByHandlerNameAsync(
            string handlerName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IEnumerable<BackgroundJobInfo>> GetActiveAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateStatusAsync(Guid id, BackgroundJobStatus status, DateTime? handledTime = null,
            string? error = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> TryTransitionStatusAsync(Guid id, BackgroundJobStatus from, BackgroundJobStatus to,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<BackgroundJobInfo>> GetDueForArmingAsync(DateTime nowUtc, int batchSize,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task MarkRetryingAsync(Guid id, DateTime nextRetryAtUtc, string? error,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task MarkRecurringRanAsync(Guid id, DateTime ranAtUtc, string? error,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> TryClaimAsync(Guid id, DateTime nowUtc, Guid runningToken,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> TryCancelWaitingAsync(Guid id, DateTime handledTimeUtc,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> TryRecordTerminalAsync(Guid id, Guid runningToken,
            BackgroundJobStatus terminalStatus, DateTime handledTimeUtc, string? error,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> TryReturnToScheduledAsync(Guid id, Guid runningToken, DateTime ranAtUtc, string? error,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> TryMarkRetryingAsync(Guid id, Guid runningToken, DateTime nextRetryAtUtc, string? error,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<BackgroundJobInfo>> GetStaleRunningAsync(DateTime cutoffUtc, int batchSize,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> TryTransitionFromArmingAsync(Guid id, Guid armingToken, BackgroundJobStatus to,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> ResetExpiredArmingClaimsAsync(DateTime now, int batchSize,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
