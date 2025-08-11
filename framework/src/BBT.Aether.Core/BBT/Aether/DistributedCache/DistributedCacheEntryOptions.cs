using System;

namespace BBT.Aether.DistributedCache;

/// <summary>
/// Options for configuring cache entry
/// </summary>
public class DistributedCacheEntryOptions
{
    /// <summary>
    /// Absolute expiration time
    /// </summary>
    public DateTimeOffset? AbsoluteExpiration { get; set; }

    /// <summary>
    /// Sliding expiration time
    /// </summary>
    public TimeSpan? SlidingExpiration { get; set; }

    /// <summary>
    /// Create options with absolute expiration
    /// </summary>
    public static DistributedCacheEntryOptions WithAbsoluteExpiration(DateTimeOffset absoluteExpiration)
        => new() { AbsoluteExpiration = absoluteExpiration };

    /// <summary>
    /// Create options with sliding expiration
    /// </summary>
    public static DistributedCacheEntryOptions WithSlidingExpiration(TimeSpan slidingExpiration)
        => new() { SlidingExpiration = slidingExpiration };
}