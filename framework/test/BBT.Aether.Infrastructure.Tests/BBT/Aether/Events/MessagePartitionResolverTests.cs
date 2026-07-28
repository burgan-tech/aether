using System;
using System.Linq;
using BBT.Aether.Events;
using Shouldly;
using Xunit;

namespace BBT.Aether.Events;

public sealed class MessagePartitionResolverTests
{
    private const int PartitionCount = 64;

    [Fact]
    public void Same_key_always_resolves_to_the_same_partition()
    {
        const string key = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

        MessagePartitionResolver.Resolve(key, PartitionCount)
            .ShouldBe(MessagePartitionResolver.Resolve(key, PartitionCount));
    }

    [Fact]
    public void Resolved_partition_is_within_range()
    {
        for (var i = 0; i < 1000; i++)
        {
            var p = MessagePartitionResolver.Resolve(Guid.NewGuid().ToString(), PartitionCount);
            p.ShouldBeGreaterThanOrEqualTo((short)0);
            p.ShouldBeLessThan((short)PartitionCount);
        }
    }

    [Fact]
    public void Distribution_across_partitions_is_reasonably_even()
    {
        var counts = new int[PartitionCount];
        const int samples = 64_000;

        for (var i = 0; i < samples; i++)
            counts[MessagePartitionResolver.Resolve(Guid.NewGuid().ToString(), PartitionCount)]++;

        var expected = samples / (double)PartitionCount;   // 1000
        counts.Min().ShouldBeGreaterThan((int)(expected * 0.8));
        counts.Max().ShouldBeLessThan((int)(expected * 1.2));
    }

    [Fact]
    public void Different_keys_resolve_independently()
    {
        MessagePartitionResolver.Resolve("instance-a", 64)
            .ShouldBe(MessagePartitionResolver.Resolve("instance-a", 64));
        MessagePartitionResolver.Resolve("instance-a", 64)
            .ShouldNotBe(MessagePartitionResolver.Resolve("instance-b", 64));
    }

    [Fact]
    public void Null_or_whitespace_key_resolves_to_partition_zero()
    {
        MessagePartitionResolver.Resolve(null, PartitionCount).ShouldBe((short)0);
        MessagePartitionResolver.Resolve("", PartitionCount).ShouldBe((short)0);
        MessagePartitionResolver.Resolve("   ", PartitionCount).ShouldBe((short)0);
    }

    [Fact]
    public void Hash_is_stable_across_runs_and_processes()
    {
        // The partition algorithm is an architectural contract: changing it redistributes
        // every existing row. These are golden values recorded from the first run of this
        // test (see MessagePartitionResolver xxhash64-mod, version 1) — not independently
        // derived by hand. If they change, the algorithm changed.
        MessagePartitionResolver.Resolve("instance-a", 64).ShouldBe(GoldenA);
        MessagePartitionResolver.Resolve("instance-b", 64).ShouldBe(GoldenB);
    }

    // Fill these in from the first run — see instructions.
    private const short GoldenA = 18;
    private const short GoldenB = 62;
}
