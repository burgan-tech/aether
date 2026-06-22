using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using BBT.Aether.Uow;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Domain.EntityFrameworkCore;

/// <inheritdoc />
public sealed class AetherDbContextProvider<TDbContext>(
    ICurrentSchema currentSchema,
    IUnitOfWorkManager unitOfWorkManager)
    : IAetherDbContextProvider<TDbContext>
    where TDbContext : DbContext
{
    /// <inheritdoc />
    public Task<TDbContext> GetDbContextAsync(CancellationToken cancellationToken = default)
    {
        var schema = currentSchema.Name;
        if (string.IsNullOrWhiteSpace(schema))
        {
            throw new InvalidOperationException("Current schema is not set.");
        }

        var uow = unitOfWorkManager.Current;
        if (uow is null)
        {
            throw new InvalidOperationException("No active UnitOfWork.");
        }

        if (uow is not IEfCoreUnitOfWork efUow)
        {
            throw new InvalidOperationException(
                "The active UnitOfWork does not support EF Core schema-bound DbContext resolution.");
        }

        return efUow.GetDbContextAsync<TDbContext>(schema, cancellationToken);
    }
}
