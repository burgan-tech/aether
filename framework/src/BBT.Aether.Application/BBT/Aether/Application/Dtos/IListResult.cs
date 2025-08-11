using System.Collections.Generic;

namespace BBT.Aether.Application.Dtos;

public interface IListResult<T>
{
    /// <summary>
    /// List of items.
    /// </summary>
    IReadOnlyList<T> Items { get; set; }
}