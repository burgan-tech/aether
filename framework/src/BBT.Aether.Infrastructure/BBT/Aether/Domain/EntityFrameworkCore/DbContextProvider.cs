using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Aether.Domain.EntityFrameworkCore;

public sealed class DbContextProvider<TDbContext>(TDbContext dbContext) : IDbContextProvider<TDbContext>
    where TDbContext : DbContext
{
    public TDbContext GetDbContext()
    {
        return dbContext;
    }
}