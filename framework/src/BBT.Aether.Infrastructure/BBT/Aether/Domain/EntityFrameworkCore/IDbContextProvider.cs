using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Domain.EntityFrameworkCore;

public interface IDbContextProvider<out TDbContext>
    where TDbContext : DbContext
{
    TDbContext GetDbContext();
}