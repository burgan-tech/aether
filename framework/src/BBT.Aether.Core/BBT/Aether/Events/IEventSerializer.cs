using System;
using System.Text.Json;

namespace BBT.Aether.Events;

/// <summary>
/// Defines interface for serializing and deserializing event envelopes.
/// </summary>
public interface IEventSerializer
{
    /// <summary>
    /// Serializes a CloudEvent envelope to bytes.
    /// </summary>
    /// <param name="envelope">The envelope to serialize</param>
    /// <returns>Serialized bytes</returns>
    byte[] Serialize(CloudEventEnvelope envelope);

    /// <summary>
    /// Serializes any object (typically a CloudEvent envelope) to bytes.
    /// Used for non-generic serialization scenarios.
    /// </summary>
    /// <param name="obj">The object to serialize</param>
    /// <returns>Serialized bytes</returns>
    byte[] Serialize(object obj);

    /// <summary>
    /// Serializes an object to a JsonElement for storage in database entities.
    /// Useful for storing structured data in JsonElement properties (e.g., BackgroundJobInfo.Payload).
    /// </summary>
    /// <param name="obj">The object to serialize</param>
    /// <returns>JsonElement representation of the object</returns>
    JsonElement SerializeToElement(object obj);

    /// <summary>
    /// Deserializes bytes to a CloudEvent envelope.
    /// </summary>
    /// <typeparam name="TOut">The output type</typeparam>
    /// <param name="payload">The bytes to deserialize</param>
    /// <returns>Deserialized envelope or null</returns>
    TOut? Deserialize<TOut>(ReadOnlySpan<byte> payload);

    /// <summary>
    /// Deserializes bytes to a CloudEvent envelope with the specified type.
    /// </summary>
    /// <param name="payload">The bytes to deserialize</param>
    /// <param name="type">The target type to deserialize to</param>
    /// <returns>Deserialized envelope or null</returns>
    object? Deserialize(ReadOnlySpan<byte> payload, Type type);
}
