using System;

namespace BBT.Aether.Events;

/// <summary>
/// Marks a property on an event class as the source for the CloudEvent subject field.
/// The property value will be automatically extracted and used as the subject when publishing events.
/// If multiple properties have this attribute, only the first one found will be used.
/// </summary>
/// <example>
/// <code>
/// [EventName("OrderCreated", version: 1)]
/// public sealed record OrderCreatedEvent
/// {
///     [EventSubject]
///     public Guid OrderId { get; init; }
///     
///     public decimal Amount { get; init; }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class EventSubjectAttribute : Attribute
{
}

