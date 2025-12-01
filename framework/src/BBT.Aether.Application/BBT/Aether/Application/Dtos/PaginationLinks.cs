namespace BBT.Aether.Application.Dtos;

/// <summary>
/// HATEOAS pagination links for API responses.
/// Provides navigation links for paginated results.
/// </summary>
public class PaginationLinks
{
    /// <summary>
    /// Link to the current page.
    /// </summary>
    public string Self { get; set; } = string.Empty;

    /// <summary>
    /// Link to the first page.
    /// </summary>
    public string First { get; set; } = string.Empty;

    /// <summary>
    /// Link to the next page. Empty if no next page exists.
    /// </summary>
    public string Next { get; set; } = string.Empty;

    /// <summary>
    /// Link to the previous page. Empty if on first page.
    /// </summary>
    public string Prev { get; set; } = string.Empty;
}

