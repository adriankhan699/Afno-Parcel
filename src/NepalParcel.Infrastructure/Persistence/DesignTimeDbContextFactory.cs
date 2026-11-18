using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NepalParcel.Infrastructure.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c> to create migrations without booting the web host.
/// Override the connection with the <c>NEPALPARCEL_DESIGN_CONNECTION</c> environment variable
/// when running <c>dotnet ef database update</c>.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<NepalParcelDbContext>
{
    private const string PlaceholderConnection =
        "Server=localhost,1433;Database=AfnoParcel;Integrated Security=false;TrustServerCertificate=True";

    public NepalParcelDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("NEPALPARCEL_DESIGN_CONNECTION") ?? PlaceholderConnection;

        var options = new DbContextOptionsBuilder<NepalParcelDbContext>()
            .UseSqlServer(connectionString, sql =>
                sql.MigrationsHistoryTable("__EFMigrationsHistory", NepalParcelDbContext.DefaultSchema))
            .Options;

        return new NepalParcelDbContext(options);
    }
}
