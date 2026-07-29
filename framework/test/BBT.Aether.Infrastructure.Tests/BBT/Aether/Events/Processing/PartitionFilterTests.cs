using System;
using System.Linq;
using BBT.Aether.Events;
using BBT.Aether.Events.Processing;
using Shouldly;
using Xunit;

namespace BBT.Aether.Events.Processing;

public sealed class PartitionFilterTests
{
    [Fact]
    public void No_signals_means_unfiltered()
    {
        // An empty result is the fallback timeout firing. Leasing unfiltered here is what
        // recovers a partition whose signal was lost.
        PartitionFilter.Resolve(Array.Empty<OutboxSignalKey>()).ShouldBeNull();
    }

    [Fact]
    public void A_check_all_signal_means_unfiltered()
    {
        var keys = new[]
        {
            new OutboxSignalKey("sys_queues", 3),
            new OutboxSignalKey("sys_queues", OutboxWakeupSignal.AllPartitions),
        };

        PartitionFilter.Resolve(keys).ShouldBeNull();
    }

    [Fact]
    public void Distinct_partitions_are_collected()
    {
        var keys = new[]
        {
            new OutboxSignalKey("sys_queues", 3),
            new OutboxSignalKey("sys_queues", 7),
            new OutboxSignalKey("sys_queues", 3),
        };

        var result = PartitionFilter.Resolve(keys);

        result.ShouldNotBeNull();
        result!.OrderBy(p => p).ShouldBe(new short[] { 3, 7 });
    }

    [Fact]
    public void Keys_from_different_schemas_are_all_included()
    {
        // Defence in depth rather than an expected case: the dispatcher scopes itself by
        // schema and the subscription endpoint rejects foreign schemas before they reach the
        // coordinator. Including them costs a wasted partition at worst, never a missed row.
        var keys = new[]
        {
            new OutboxSignalKey("sys_queues", 1),
            new OutboxSignalKey("other", 2),
        };

        var result = PartitionFilter.Resolve(keys);

        result.ShouldNotBeNull();
        result!.Count.ShouldBe(2);
    }

    [Fact]
    public void A_single_check_all_key_on_its_own_means_unfiltered()
    {
        var keys = new[] { new OutboxSignalKey("sys_queues", OutboxWakeupSignal.AllPartitions) };

        PartitionFilter.Resolve(keys).ShouldBeNull();
    }
}
