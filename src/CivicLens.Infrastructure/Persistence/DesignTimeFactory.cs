using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CivicLens.Infrastructure.Persistence;

/// <summary>Used only by `dotnet ef` to create migrations without starting the API.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
                 ?? "Host=localhost;Port=5432;Database=civiclens;Username=civiclens;Password=civiclens_dev";
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs, o => o.UseNetTopologySuite()).Options;
        return new AppDbContext(options);
    }
}
