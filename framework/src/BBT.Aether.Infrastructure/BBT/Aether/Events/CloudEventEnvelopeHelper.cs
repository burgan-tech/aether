using System;

namespace BBT.Aether.Events;

/// <summary>
/// Helper class for parsing and extracting data from CloudEventEnvelope.
/// Provides common functionality used by job dispatchers and event processors.
/// </summary>
internal static class CloudEventEnvelopeHelper
{
    /// <summary>
    /// Attempts to parse the payload as a CloudEventEnvelope.
    /// Returns null if the payload is not in envelope format (legacy format).
    /// </summary>
    /// <param name="eventSerializer">The event serializer to use for deserialization.</param>
    /// <param name="payload">The raw payload bytes to parse.</param>
    /// <returns>The parsed envelope or null if parsing fails or payload is not in envelope format.</returns>
    public static CloudEventEnvelope? TryParseEnvelope(IEventSerializer eventSerializer, byte[] payload)
    {
        try
        {
            var envelope = eventSerializer.Deserialize<CloudEventEnvelope>(payload);

            // Validate it's actually an envelope by checking required properties
            if (envelope != null && !string.IsNullOrWhiteSpace(envelope.Type))
            {
                return envelope;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts the data payload from a CloudEventEnvelope, serializing it back to bytes.
    /// If payload is not in envelope format, returns the original payload.
    /// </summary>
    /// <param name="eventSerializer">The event serializer to use.</param>
    /// <param name="payload">The raw payload bytes.</param>
    /// <param name="envelope">Output: the parsed envelope if successful, null otherwise.</param>
    /// <returns>The data payload bytes (either from envelope.Data or original payload).</returns>
    public static ReadOnlyMemory<byte> ExtractDataPayload(
        IEventSerializer eventSerializer,
        ReadOnlyMemory<byte> payload,
        out CloudEventEnvelope? envelope)
    {
        envelope = TryParseEnvelope(eventSerializer, payload.ToArray());

        if (envelope != null)
        {
            var argsBytes = eventSerializer.Serialize(envelope.Data);
            return new ReadOnlyMemory<byte>(argsBytes);
        }

        return payload;
    }
}

