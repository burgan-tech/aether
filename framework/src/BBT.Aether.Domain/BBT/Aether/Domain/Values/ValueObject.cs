using System.Collections.Generic;

namespace BBT.Aether.Domain.Values;

/// <summary>
/// Base class for value objects.
/// </summary>
public abstract class ValueObject
{
    /// <summary>
    /// Gets the atomic values of the value object.
    /// </summary>
    /// <returns>The atomic values.</returns>
    protected abstract IEnumerable<object> GetAtomicValues();

    /// <summary>
    /// Determines whether the specified object is equal to the current object.
    /// </summary>
    /// <param name="obj">The object to compare with the current object.</param>
    /// <returns>
    ///   <see langword="true"/> if the specified object is equal to the current object; otherwise, <see langword="false"/>.
    /// </returns>
    public bool ValueEquals(object obj)
    {
        if (obj == null || obj.GetType() != GetType())
        {
            return false;
        }

        var other = (ValueObject)obj;

        var thisValues = GetAtomicValues().GetEnumerator();
        var otherValues = other.GetAtomicValues().GetEnumerator();

        var thisMoveNext = thisValues.MoveNext();
        var otherMoveNext = otherValues.MoveNext();
        while (thisMoveNext && otherMoveNext)
        {
            if (ReferenceEquals(thisValues.Current, null) ^ ReferenceEquals(otherValues.Current, null))
            {
                return false;
            }

            if (thisValues.Current is ValueObject currentValueObject && otherValues.Current is ValueObject otherValueObject)
            {
                if (!currentValueObject.ValueEquals(otherValueObject))
                {
                    return false;
                }
            }
            else if (thisValues.Current != null && !thisValues.Current.Equals(otherValues.Current))
            {
                return false;
            }

            thisMoveNext = thisValues.MoveNext();
            otherMoveNext = otherValues.MoveNext();

            if (thisMoveNext != otherMoveNext)
            {
                return false;
            }
        }

        return !thisMoveNext && !otherMoveNext;
    }
}