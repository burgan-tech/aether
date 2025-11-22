using System;
using System.Text.Json;
using BBT.Aether.Events;

namespace BBT.Aether.Events;

/// <summary>
/// System.Text.Json-based implementation of IEventSerializer using camelCase naming policy.
/// </summary>
public sealed class SystemTextJsonEventSerializer : IEventSerializer
{
    private readonly static JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <inheritdoc />
    public byte[] Serialize(CloudEventEnvelope envelope)
    {
        return JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
    }

    /// <inheritdoc />
    public byte[] Serialize(object obj)
    {
        return JsonSerializer.SerializeToUtf8Bytes(obj, Options);
    }

    /// <inheritdoc />
    public TOut? Deserialize<TOut>(ReadOnlySpan<byte> payload)
    {
        return JsonSerializer.Deserialize<TOut>(payload, Options);
    }

    /// <inheritdoc />
    public object? Deserialize(ReadOnlySpan<byte> payload, Type type)
    {
        return JsonSerializer.Deserialize(payload, type, Options);
    }
}
