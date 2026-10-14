using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ScanBack.Infrastructure;

/// <summary>
/// Allows EF tooling to generate SQL Server migrations without starting workers
/// or requiring a running database. Runtime keys must be persisted separately.
/// </summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Server=localhost;Database=ScanBack;Integrated Security=true;Encrypt=true";
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options;
        return new AppDbContext(options, new EphemeralDataProtectionProvider());
    }
}
