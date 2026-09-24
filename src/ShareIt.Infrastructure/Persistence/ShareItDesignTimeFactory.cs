using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ShareIt.Infrastructure.Persistence;

public sealed class ShareItDesignTimeFactory : IDesignTimeDbContextFactory<ShareItDbContext>
{
    public ShareItDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ShareItDbContext>()
        .UseSqlite("Data Source=shareit-design.db").Options);
}
