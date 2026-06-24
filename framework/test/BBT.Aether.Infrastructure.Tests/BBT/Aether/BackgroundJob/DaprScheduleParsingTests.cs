using BBT.Aether.BackgroundJob.Dapr;
using Shouldly;
using Xunit;

namespace BBT.Aether.BackgroundJob;

/// <summary>
/// Pure unit tests for <see cref="DaprJobScheduler.DetectKind"/>. These exercise the schedule-string
/// classification logic without a Dapr client or container.
/// </summary>
public class DaprScheduleParsingTests
{
    [Theory]
    [InlineData("*/5 * * * *")]
    [InlineData("0 0 * * *")]
    [InlineData("0 0 12 * * *")] // 6-field cron
    [InlineData("@every 1h")]
    [InlineData("@daily")]
    [InlineData("@hourly")]
    [InlineData("@weekly")]
    [InlineData("@monthly")]
    [InlineData("@yearly")]
    public void DetectKind_ShouldClassifyCronAndPeriodExpressionsAsCron(string schedule)
    {
        DaprJobScheduler.DetectKind(schedule).ShouldBe(DaprJobScheduler.ScheduleKind.Cron);
    }

    [Theory]
    [InlineData("2026-07-01T10:00:00Z")]
    [InlineData("2026-07-01T10:00:00+03:00")]
    [InlineData("2026-12-31T23:59:59.123Z")]
    public void DetectKind_ShouldClassifyIso8601InstantsAsInstant(string schedule)
    {
        DaprJobScheduler.DetectKind(schedule).ShouldBe(DaprJobScheduler.ScheduleKind.Instant);
    }

    [Theory]
    [InlineData("PT30S")]
    [InlineData("PT1H30M")]
    [InlineData("00:00:30")]
    [InlineData("01:15:00")]
    public void DetectKind_ShouldClassifyDurationsAsDuration(string schedule)
    {
        DaprJobScheduler.DetectKind(schedule).ShouldBe(DaprJobScheduler.ScheduleKind.Duration);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-schedule")]
    public void DetectKind_ShouldFallBackToCronForUnrecognizedStrings(string schedule)
    {
        DaprJobScheduler.DetectKind(schedule).ShouldBe(DaprJobScheduler.ScheduleKind.Cron);
    }
}
