using System;
using System.IO.Hashing;
using System.Text;

namespace BBT.Aether.Events;

/// <summary>
/// Maps a message partition key to a logical partition using a deterministic hash.
/// </summary>
/// <remarks>
/// <para>
/// The algorithm is an architectural contract: changing it redistributes every existing
/// row across partitions. It is versioned as <c>xxhash64-mod</c> / <c>partitionVersion 1</c>.
/// </para>
/// <para>
/// <see cref="string.GetHashCode()"/> must never be used here — .NET randomises it per
/// process, so the same key would map to different partitions on different pods.
/// </para>
/// </remarks>
public static class MessagePartitionResolver
{
    /// <summary>The partition algorithm identifier, for documentation and diagnostics.</summary>
    public const string Algorithm = "xxhash64-mod";

    /// <summary>The partition algorithm version. Bumping this requires a migration plan.</summary>
    public const int Version = 1;

    /// <summary>
    /// Resolves the logical partition for <paramref name="partitionKey"/>.
    /// Returns 0 when the key is null or blank. Note 0 is a normal partition, not a sentinel.
    /// </summary>
    public static short Resolve(string? partitionKey, int partitionCount)
    {
        if (partitionCount <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(partitionCount), partitionCount, "Partition count must be positive.");

        if (string.IsNullOrWhiteSpace(partitionKey)) return 0;

        var hash = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(partitionKey));
        return (short)(hash % (ulong)partitionCount);
    }
}
