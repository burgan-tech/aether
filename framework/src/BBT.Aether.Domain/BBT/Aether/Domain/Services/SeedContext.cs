using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace BBT.Aether.Domain.Services;

/// <summary>
/// Context for data seeding operations, allowing the passing of properties and configurations.
/// </summary>
public class SeedContext
{
    /// <summary>
    /// Gets/sets a key-value on the <see cref="Properties"/>.
    /// </summary>
    /// <param name="name">Name of the property</param>
    /// <returns>
    /// Returns the value in the <see cref="Properties"/> dictionary by given <paramref name="name"/>.
    /// Returns null if given <paramref name="name"/> is not present in the <see cref="Properties"/> dictionary.
    /// </returns>
    public object? this[string name] {
        get => Properties.GetOrDefault(name);
        set => Properties[name] = value;
    }

    /// <summary>
    /// Can be used to get/set custom properties.
    /// </summary>
    [NotNull]
    public Dictionary<string, object?> Properties { get; } = new();

    /// <summary>
    /// Sets a property in the <see cref="Properties"/> dictionary.
    /// This is a shortcut for nested calls on this object.
    /// </summary>
    /// <param name="key">The property key.</param>
    /// <param name="value">The property value.</param>
    /// <returns>The current <see cref="SeedContext"/> instance.</returns>
    public virtual SeedContext WithProperty(string key, object? value)
    {
        Properties[key] = value;
        return this;
    }
}