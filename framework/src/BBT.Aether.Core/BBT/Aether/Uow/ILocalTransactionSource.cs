using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Uow;

/// <summary>
/// Represents a data source that can participate in a unit of work.
/// Each data provider (EF Core, Dapper, MongoDB) implements this interface.
/// </summary>
public interface ILocalTransactionSource
{
    /// <summary>
    /// Gets the unique name of this transaction source (e.g., "efcore:MyDbContext").
    /// </summary>
    string SourceName { get; }

    /// <summary>
    /// Creates a new local transaction based on the unit of work options.
    /// </summary>
    /// <param name="options">The unit of work options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A local transaction instance</returns>
    Task<ILocalTransaction> CreateTransactionAsync(UnitOfWorkOptions options, CancellationToken cancellationToken = default);
}

