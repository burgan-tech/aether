using System;
using BBT.Aether.Clock;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BBT.Aether.Domain.EntityFrameworkCore.ValueComparers;

/// <summary>
/// Value converter that normalizes <see cref="DateTimeOffset"/> values through <see cref="IClock.NormalizeToUtc(DateTimeOffset)"/>
/// on both the read and write paths, ensuring consistent UTC offset across all entity properties.
/// </summary>
public sealed class ClockDateTimeOffsetValueConverter(IClock clock)
    : ValueConverter<DateTimeOffset, DateTimeOffset>(
        v => clock.NormalizeToUtc(v),
        v => clock.NormalizeToUtc(v))
{
}
