using System;
using BBT.Aether.Events;
using Shouldly;
using Xunit;

namespace BBT.Aether.Events.Processing;

public sealed class CleanupScheduleTests
{
    [Fact]
    public void Outbox_cleanup_defaults_match_inbox()
    {
        var outbox = new AetherOutboxOptions();
        var inbox = new AetherInboxOptions();

        outbox.CleanupInterval.ShouldBe(inbox.CleanupInterval);
        outbox.CleanupBatchSize.ShouldBe(inbox.CleanupBatchSize);
    }

    [Theory]
    // (minutes since last run, interval in minutes, should run)
    [InlineData(0, 60, false)]
    [InlineData(59, 60, false)]
    [InlineData(60, 60, true)]
    [InlineData(3600, 60, true)]
    public void IsDue_respects_interval(int elapsedMinutes, int intervalMinutes, bool expected)
    {
        var now = new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
        var lastRun = now.AddMinutes(-elapsedMinutes);

        CleanupSchedule
            .IsDue(lastRun, now, TimeSpan.FromMinutes(intervalMinutes))
            .ShouldBe(expected);
    }

    [Fact]
    public void IsDue_is_true_on_first_run()
    {
        CleanupSchedule
            .IsDue(DateTime.MinValue, DateTime.UtcNow, TimeSpan.FromHours(1))
            .ShouldBeTrue();
    }

    [Fact]
    public void Inbox_and_outbox_share_the_same_cleanup_schedule_policy()
    {
        var now = new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
        var interval = TimeSpan.FromHours(1);

        CleanupSchedule.IsDue(now.AddMinutes(-30), now, interval).ShouldBeFalse();
        CleanupSchedule.IsDue(now.AddMinutes(-90), now, interval).ShouldBeTrue();
    }

    [Fact]
    public void Error_and_idle_polling_intervals_are_independent_options()
    {
        var outbox = new AetherOutboxOptions { MaxPollingInterval = TimeSpan.FromMinutes(5) };
        var inbox = new AetherInboxOptions { MaxPollingInterval = TimeSpan.FromMinutes(5) };

        // Raising the idle ceiling must not lengthen error recovery.
        outbox.ErrorPollingInterval.ShouldBe(TimeSpan.FromSeconds(60));
        inbox.ErrorPollingInterval.ShouldBe(TimeSpan.FromSeconds(60));
    }
}
