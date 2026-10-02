using System;
using System.Buffers;
using System.Text.Json;
using BBT.Aether.Events;

namespace BBT.Aether.BackgroundJob;

/// <summary>
/// Builds the bytes handed to the external scheduler, and reads them back on the callback.
/// <para>
/// A job's payload is persisted in full in <c>BackgroundJobs.Payload</c>, but the scheduler is armed
/// with a <b>reference</b>: the same CloudEvent envelope minus its <c>data</c> member, plus two
/// extension attributes (<see cref="PayloadRefExtension"/>, <see cref="JobIdExtension"/>). The armed
/// message is therefore small and bounded regardless of how large the payload is — which is the point:
/// Dapr keeps one-shot jobs in etcd, whose practical ceiling (~2 MiB) is far below what an HTTP request
/// may carry, so an oversized body used to fail the arm <i>after</i> the caller's transaction had
/// already committed.
/// </para>
/// <para>
/// The envelope header is deliberately preserved. <c>schema</c> in particular is read by
/// <c>DaprJobExecutionBridge</c> to establish the multi-schema scope <b>before</b> the job row is read;
/// dropping it would send that read to the wrong schema. <c>type</c> must survive too, because
/// <see cref="CloudEventEnvelopeHelper.TryParseEnvelope"/> uses it to recognise an envelope at all.
/// </para>
/// <para>
/// Both arm paths — the inline/deferred arm in <c>BackgroundJobService</c> and the recovery arm in
/// <c>BackgroundJobArmingProcessor</c> — go through <see cref="CreateReference"/>, so they cannot
/// drift apart. They must not: the recovery path is what re-arms a job after a scheduler outage, and
/// an oversized recovery arm would re-create the original failure at exactly the worst moment.
/// </para>
/// </summary>
internal static class JobArmPayload
{
    /// <summary>Extension attribute marking an armed message as carrying no <c>data</c> member.</summary>
    internal const string PayloadRefExtension = "payloadref";

    /// <summary>Extension attribute carrying the job row's primary key.</summary>
    internal const string JobIdExtension = "jobid";

    private const string DataProperty = "data";
    private const string ExtensionsProperty = "extensions";

    /// <summary>
    /// Produces the reference bytes to arm with, from the envelope element persisted on the job row.
    /// Every member except <c>data</c> is copied verbatim; <c>extensions</c> is merged so a caller's own
    /// extension attributes survive alongside the two this method adds.
    /// </summary>
    /// <param name="storedEnvelope">The serialized envelope held in <c>BackgroundJobInfo.Payload</c>.</param>
    /// <param name="jobId">The job row's primary key, echoed back on the callback for verification.</param>
    internal static byte[] CreateReference(JsonElement storedEnvelope, Guid jobId)
    {
        if (storedEnvelope.ValueKind != JsonValueKind.Object)
        {
            // Not an envelope we can strip. Arming the raw text is the pre-existing behaviour and is
            // still correct — it is only unbounded, which a non-envelope payload is not in practice.
            return JsonSerializer.SerializeToUtf8Bytes(storedEnvelope);
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            foreach (var member in storedEnvelope.EnumerateObject())
            {
                // The body is what we are deliberately leaving behind.
                if (member.NameEquals(DataProperty))
                    continue;

                // Rewritten below so caller extensions and ours end up in one object.
                if (member.NameEquals(ExtensionsProperty))
                    continue;

                member.WriteTo(writer);
            }

            writer.WritePropertyName(ExtensionsProperty);
            writer.WriteStartObject();

            if (storedEnvelope.TryGetProperty(ExtensionsProperty, out var existing)
                && existing.ValueKind == JsonValueKind.Object)
            {
                foreach (var extension in existing.EnumerateObject())
                {
                    // Ours win on collision: a stale marker from a re-serialized envelope must not
                    // survive and misdescribe the message we are arming now.
                    if (extension.NameEquals(PayloadRefExtension) || extension.NameEquals(JobIdExtension))
                        continue;

                    extension.WriteTo(writer);
                }
            }

            writer.WriteBoolean(PayloadRefExtension, true);
            writer.WriteString(JobIdExtension, jobId);

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// True when an armed message carries no body and the handler arguments must be rehydrated from the
    /// job row. <paramref name="jobId"/> is the id the armer recorded, or <see cref="Guid.Empty"/> when
    /// the message predates that extension.
    /// </summary>
    internal static bool IsReference(CloudEventEnvelope? envelope, out Guid jobId)
    {
        jobId = Guid.Empty;

        if (envelope?.Extensions is not { } extensions)
            return false;

        if (!extensions.TryGetValue(PayloadRefExtension, out var marker) || !IsTrue(marker))
            return false;

        if (extensions.TryGetValue(JobIdExtension, out var id))
            _ = TryReadGuid(id, out jobId);

        return true;
    }

    /// <summary>
    /// Reads the handler arguments out of the envelope element persisted on the job row.
    /// Returns false when the row carries no <c>data</c> member — the body is gone, and the caller must
    /// fail the job loudly rather than invoke a handler with nothing.
    /// </summary>
    internal static bool TryExtractStoredData(JsonElement storedEnvelope, out ReadOnlyMemory<byte> data)
    {
        data = default;

        if (storedEnvelope.ValueKind != JsonValueKind.Object
            || !storedEnvelope.TryGetProperty(DataProperty, out var body)
            || body.ValueKind == JsonValueKind.Undefined)
        {
            return false;
        }

        data = JsonSerializer.SerializeToUtf8Bytes(body);
        return true;
    }

    /// <summary>
    /// Extension values arrive as <see cref="JsonElement"/> after a round trip through JSON, but as a
    /// real <see cref="bool"/> when the envelope object is still in memory. Accept both.
    /// </summary>
    private static bool IsTrue(object? value) => value switch
    {
        bool flag => flag,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.String } element
            => bool.TryParse(element.GetString(), out var parsed) && parsed,
        string text => bool.TryParse(text, out var parsed) && parsed,
        _ => false
    };

    private static bool TryReadGuid(object? value, out Guid id)
    {
        switch (value)
        {
            case Guid guid:
                id = guid;
                return true;
            case JsonElement { ValueKind: JsonValueKind.String } element:
                return Guid.TryParse(element.GetString(), out id);
            case string text:
                return Guid.TryParse(text, out id);
            default:
                id = Guid.Empty;
                return false;
        }
    }
}
