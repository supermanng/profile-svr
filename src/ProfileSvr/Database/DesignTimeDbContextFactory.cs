using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ProfileSvr.Database;

/// <summary>
/// Lets `dotnet ef migrations add` build the model without a live database connection.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        // Use the real connection string when present (e.g. `dotnet ef database update`);
        // otherwise a placeholder is enough to build the model for `migrations add`.
        var connectionString =
            Environment.GetEnvironmentVariable("PROFILESVR_DB")
            ?? "Server=localhost;Port=3306;Database=profilesvr;User=root;Password=design-time-only";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 36)))
            .Options;

        return new AppDbContext(options);
    }
}
