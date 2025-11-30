namespace System;

/// <summary>
/// Extension methods for the <see cref="DateTime"/>.
/// </summary>
public static class AetherDateTimeExtensions
{
    public static DateTime ClearTime(this DateTime dateTime)
    {
        return dateTime.Subtract(
            new TimeSpan(
                0,
                dateTime.Hour,
                dateTime.Minute,
                dateTime.Second,
                dateTime.Millisecond
            )
        );
    }

    public static DateTime EnsureUtc(this DateTime value)
    {
        if (value == default)
            return value;

        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value
        };
    }

    public static DateTime? EnsureUtc(this DateTime? value)
        => value?.EnsureUtc();
}