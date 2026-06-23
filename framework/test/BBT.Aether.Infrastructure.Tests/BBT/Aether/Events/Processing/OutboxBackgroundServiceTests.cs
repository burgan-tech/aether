using System;
using BBT.Aether.Events;
using Shouldly;
using Xunit;

namespace BBT.Aether.Events.Processing;

public sealed class AdaptivePollingTests
{
    private static TimeSpan NextDelay(TimeSpan current, int processed, AetherOutboxOptions opts)
    {
        if (processed > 0) return opts.BusyPollingInterval;
        var next = TimeSpan.FromMilliseconds(current.TotalMilliseconds * 2);
        return next > opts.MaxPollingInterval ? opts.MaxPollingInterval : next;
    }

    [Fact]
    public void Busy_returns_busy_interval()
    {
        var opts = new AetherOutboxOptions
        {
            BusyPollingInterval = TimeSpan.FromMilliseconds(100),
            IdlePollingInterval = TimeSpan.FromSeconds(5),
            MaxPollingInterval  = TimeSpan.FromSeconds(60),
        };
        NextDelay(opts.IdlePollingInterval, processed: 10, opts)
            .ShouldBe(opts.BusyPollingInterval);
    }

    [Fact]
    public void Idle_doubles_delay_each_round()
    {
        var opts = new AetherOutboxOptions
        {
            BusyPollingInterval = TimeSpan.FromMilliseconds(100),
            IdlePollingInterval = TimeSpan.FromSeconds(5),
            MaxPollingInterval  = TimeSpan.FromSeconds(60),
        };
        var d1 = NextDelay(opts.IdlePollingInterval, processed: 0, opts); // 10s
        var d2 = NextDelay(d1, processed: 0, opts);                        // 20s
        var d3 = NextDelay(d2, processed: 0, opts);                        // 40s
        var d4 = NextDelay(d3, processed: 0, opts);                        // 60s (capped)
        var d5 = NextDelay(d4, processed: 0, opts);                        // 60s (stays capped)

        d1.ShouldBe(TimeSpan.FromSeconds(10));
        d2.ShouldBe(TimeSpan.FromSeconds(20));
        d3.ShouldBe(TimeSpan.FromSeconds(40));
        d4.ShouldBe(TimeSpan.FromSeconds(60));
        d5.ShouldBe(TimeSpan.FromSeconds(60));
    }
}
