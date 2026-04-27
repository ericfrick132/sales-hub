using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SalesHub.Infrastructure.Persistence;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("SALESHUB_DB_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=saleshub;Username=saleshub;Password=saleshub";
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(conn)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new ApplicationDbContext(options);
    }
}
