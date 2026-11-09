using Microsoft.EntityFrameworkCore;

namespace SumppumpSite.Data;

public class SumpDb(DbContextOptions<SumpDb> options) : DbContext(options)
{
    public DbSet<Reading> Readings => Set<Reading>();
}
