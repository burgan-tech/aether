namespace BBT.Aether.Domain.Repositories;

/// <summary>
/// Provides parameters for pagination.
/// </summary>
public class PaginationParameters
{
    private const int LimitResultCount = 50;
    private int _maxResultCount = 10;
    private int _skipCount = 0;

    /// <summary>
    /// Gets or sets the sorting criteria.
    /// </summary>
    public string? Sorting { get; set; }
    /// <summary>
    /// Gets or sets the number of items to skip.
    /// </summary>
    public int SkipCount {
        get => _skipCount;
        set => _skipCount = value;
    }

    /// <summary>
    /// Gets or sets the maximum number of results to return.
    /// </summary>
    public int MaxResultCount {
        get => _maxResultCount;
        set => _maxResultCount = value > LimitResultCount ? LimitResultCount : value < 1 ? 1 : value;
    }
}