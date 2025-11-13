using System;

namespace BBT.Aether.Aspects;

/// <summary>
/// Attribute to mark method parameters that should be added as individual enrichment properties in logs.
/// When applied to a parameter, the parameter value will be serialized and added to the log enrichment
/// with either the custom Name or the parameter name if Name is not specified.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
public class EnrichAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the custom enrichment property name.
    /// If not specified, the parameter name will be used.
    /// </summary>
    public string? Name { get; set; }
}

