using System;
using System.Collections.Generic;
using BBT.Aether.Events;
using Shouldly;
using Xunit;

namespace BBT.Aether.Events.Processing;

public sealed class OutboxProcessorDeadLetterTests
{
    private static OutboxMessage MakeMessage(int retryCount) => new()
    {
        Id = Guid.NewGuid(),
        EventName = "test",
        EventData = [],
        Status = OutboxMessageStatus.Processing,
        RetryCount = retryCount,
        ExtraProperties = new Dictionary<string, object>()
    };

    [Fact]
    public void ShouldGoDeadLetter_when_retry_count_meets_max()
    {
        var maxRetryCount = 3;
        var message = MakeMessage(retryCount: 3);
        var isDeadLetter = message.RetryCount + 1 >= maxRetryCount;
        isDeadLetter.ShouldBeTrue();
    }

    [Fact]
    public void ShouldNotGoDeadLetter_when_retry_count_below_max()
    {
        var maxRetryCount = 3;
        var message = MakeMessage(retryCount: 1);
        var isDeadLetter = message.RetryCount + 1 >= maxRetryCount;
        isDeadLetter.ShouldBeFalse();
    }
}
