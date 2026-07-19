using System;
using System.Linq;
using System.Threading;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.Repositories;
using Shouldly;
using Xunit;

namespace BBT.Aether.BackgroundJob;

public sealed class JobStoreApiCompatibilityTests
{
    [Fact]
    public void Arming_transition_keeps_legacy_abstract_contract_and_adds_safe_default_bridge()
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
        guarded.ShouldNotBeNull();
        guarded!.IsAbstract.ShouldBeFalse(
            "the guarded overload needs a default bridge so legacy implementers remain source-compatible");
        guarded.GetParameters()[4].IsOptional.ShouldBeFalse(
            "requiring the fifth argument prevents ambiguity with legacy calls that pass a default literal token");

        foreach (var methodName in new[]
                 {
                     nameof(IJobStore.TryAcquireTerminalArmingCompensationAsync),
                     nameof(IJobStore.TryRenewArmingCompensationAsync),
                     nameof(IJobStore.TryReleaseArmingCompensationAsync)
                 })
        {
            var method = typeof(IJobStore).GetMethod(methodName);
            method.ShouldNotBeNull();
            method!.IsAbstract.ShouldBeFalse(
                $"{methodName} must safely default for legacy custom stores");
        }
    }
}
