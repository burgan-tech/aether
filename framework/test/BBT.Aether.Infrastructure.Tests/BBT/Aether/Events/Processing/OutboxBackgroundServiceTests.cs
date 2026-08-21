using System;
using BBT.Aether.Events;
using BBT.Aether.Polling;
using Shouldly;
using Xunit;

namespace BBT.Aether.Events.Processing;

/// <summary>
/// Pins the adaptive poll pacing that the outbox and inbox loops share. These exercise
/// <see cref="PollingDelay"/> itself — the production code — rather than a copy of its arithmetic, so
/// a change to the pacing rules cannot pass unnoticed.
/// </summary>
public sealed class AdaptivePollingTests
{
    private static AetherOutboxOptions Options() => new()
    {
        BusyPollingInterval = TimeSpan.FromMilliseconds(100),
        IdlePollingInterval = TimeSpan.FromSeconds(5),
        MaxPollingInterval  = TimeSpan.FromSeconds(60),
    };

    [Fact]
    public void Busy_returns_busy_interval()
    {
        var opts = Options();
        PollingDelay.OnProcessed(opts.BusyPollingInterval).ShouldBe(opts.BusyPollingInterval);
    }

    [Fact]
    public void Idle_doubles_delay_each_round_and_caps()
    {
        var opts = Options();
        var d1 = PollingDelay.OnEmpty(opts.IdlePollingInterval, opts.MaxPollingInterval); // 10s
        var d2 = PollingDelay.OnEmpty(d1, opts.MaxPollingInterval);                        // 20s
        var d3 = PollingDelay.OnEmpty(d2, opts.MaxPollingInterval);                        // 40s
        var d4 = PollingDelay.OnEmpty(d3, opts.MaxPollingInterval);                        // 60s capped
        var d5 = PollingDelay.OnEmpty(d4, opts.MaxPollingInterval);                        // stays capped

        d1.ShouldBe(TimeSpan.FromSeconds(10));
        d2.ShouldBe(TimeSpan.FromSeconds(20));
        d3.ShouldBe(TimeSpan.FromSeconds(40));
        d4.ShouldBe(opts.MaxPollingInterval);
        d5.ShouldBe(opts.MaxPollingInterval);
    }

    [Fact]
    public void Error_backs_off_one_step_instead_of_jumping_to_the_cap()
    {
        // The old behaviour set the delay to MaxPollingInterval on any exception, so one transient
        // fault stalled every replica for a full minute. Escalation keeps a blip cheap.
        var opts = Options();

        var first = PollingDelay.OnError(opts.BusyPollingInterval, opts.IdlePollingInterval, opts.MaxPollingInterval);

        first.ShouldBe(opts.IdlePollingInterval);
        first.ShouldBeLessThan(opts.MaxPollingInterval);
    }

    [Fact]
    public void Error_never_retries_at_the_busy_cadence()
    {
        var opts = Options();

        // Straight after a busy round the delay is 100 ms; doubling alone would retry a hard failure
        // 5 times a second, so the idle interval is the floor.
        PollingDelay.OnError(TimeSpan.FromMilliseconds(100), opts.IdlePollingInterval, opts.MaxPollingInterval)
            .ShouldBeGreaterThanOrEqualTo(opts.IdlePollingInterval);
    }

    [Fact]
    public void Repeated_errors_still_escalate_to_the_cap()
    {
        var opts = Options();
        var d = opts.BusyPollingInterval;
        for (var i = 0; i < 10; i++)
            d = PollingDelay.OnError(d, opts.IdlePollingInterval, opts.MaxPollingInterval);

        d.ShouldBe(opts.MaxPollingInterval);
    }

    [Theory]
    [InlineData(0.0, 0.75)]
    [InlineData(0.5, 1.00)]
    [InlineData(1.0, 1.25)]
    public void Jitter_spans_the_configured_fraction_either_side(double sample, double expectedScale)
    {
        var nominal = TimeSpan.FromSeconds(60);

        var jittered = PollingDelay.Jitter(nominal, sample);

        jittered.TotalSeconds.ShouldBe(60 * expectedScale, tolerance: 0.001);
    }

    [Fact]
    public void Jitter_never_returns_a_non_positive_delay()
    {
        PollingDelay.Jitter(TimeSpan.Zero, 0.0).ShouldBeGreaterThan(TimeSpan.Zero);
        PollingDelay.Jitter(TimeSpan.FromTicks(1), 0.0).ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void Jitter_keeps_replicas_from_sharing_a_phase()
    {
        // Two replicas holding the same nominal delay must not wake together.
        var nominal = TimeSpan.FromSeconds(60);

        PollingDelay.Jitter(nominal, 0.1).ShouldNotBe(PollingDelay.Jitter(nominal, 0.9));
    }

    [Fact]
    public void Startup_offset_stays_within_the_idle_interval()
    {
        var opts = Options();

        PollingDelay.StartupOffset(opts.IdlePollingInterval, 0.0).ShouldBe(TimeSpan.Zero);
        PollingDelay.StartupOffset(opts.IdlePollingInterval, 0.999)
            .ShouldBeLessThan(opts.IdlePollingInterval);
    }
}
