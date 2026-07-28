using System;
using BBT.Aether.Events;
using BBT.Aether.Events.Processing;
using Shouldly;
using Xunit;

namespace BBT.Aether.Events.Processing;

public sealed class AdaptivePollingTests
{
    private static readonly AetherOutboxOptions Opts = new()
    {
        BatchSize           = 100,
        BusyPollingInterval = TimeSpan.FromMilliseconds(100),
        IdlePollingInterval = TimeSpan.FromSeconds(5),
        MaxPollingInterval  = TimeSpan.FromSeconds(60),
    };

    private static TimeSpan Next(TimeSpan current, int processed) =>
        AdaptivePolling.NextDelay(
            current, processed, Opts.BatchSize,
            Opts.BusyPollingInterval, Opts.IdlePollingInterval, Opts.MaxPollingInterval);

    [Fact]
    public void Full_batch_returns_busy_interval()
    {
        Next(Opts.IdlePollingInterval, processed: 100).ShouldBe(Opts.BusyPollingInterval);
    }

    [Fact]
    public void Partial_batch_returns_idle_interval_not_busy()
    {
        // Kuyruk boşaldı: 100'lük batch'te 1 mesaj geldi. 100 ms'e düşmek 10 poll'lük
        // gereksiz tırmanma üretiyordu.
        Next(TimeSpan.FromSeconds(60), processed: 1).ShouldBe(Opts.IdlePollingInterval);
        Next(TimeSpan.FromSeconds(60), processed: 99).ShouldBe(Opts.IdlePollingInterval);
    }

    [Fact]
    public void Idle_doubles_delay_each_round()
    {
        var d1 = Next(Opts.IdlePollingInterval, processed: 0); // 10s
        var d2 = Next(d1, processed: 0);                       // 20s
        var d3 = Next(d2, processed: 0);                       // 40s
        var d4 = Next(d3, processed: 0);                       // 60s (capped)
        var d5 = Next(d4, processed: 0);                       // 60s (stays capped)

        d1.ShouldBe(TimeSpan.FromSeconds(10));
        d2.ShouldBe(TimeSpan.FromSeconds(20));
        d3.ShouldBe(TimeSpan.FromSeconds(40));
        d4.ShouldBe(TimeSpan.FromSeconds(60));
        d5.ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Partial_batch_then_idle_climbs_from_idle_interval_not_from_busy()
    {
        var afterPartial = Next(TimeSpan.FromSeconds(60), processed: 3);
        var afterEmpty   = Next(afterPartial, processed: 0);

        afterPartial.ShouldBe(TimeSpan.FromSeconds(5));
        afterEmpty.ShouldBe(TimeSpan.FromSeconds(10));
    }
}
