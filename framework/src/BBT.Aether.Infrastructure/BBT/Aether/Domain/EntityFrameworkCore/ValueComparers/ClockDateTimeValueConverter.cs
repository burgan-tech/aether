using System;
using BBT.Aether.Clock;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BBT.Aether.Domain.EntityFrameworkCore.ValueComparers;

/// <summary>
/// Value converter that normalizes <see cref="DateTime"/> values through <see cref="IClock.NormalizeToUtc(DateTime)"/>
/// on both the read and write paths, ensuring consistent UTC <see cref="DateTimeKind"/> across all entity properties.
/// </summary>
public sealed class ClockDateTimeValueConverter(IClock clock)
    : ValueConverter<DateTime, DateTime>(
        v => clock.NormalizeToUtc(v),
        v => clock.NormalizeToUtc(v))
{
}
