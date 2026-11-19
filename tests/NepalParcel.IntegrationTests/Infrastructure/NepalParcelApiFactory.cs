using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NepalParcel.Infrastructure.Persistence;
using NepalParcel.Infrastructure.Persistence.Interceptors;

namespace NepalParcel.IntegrationTests.Infrastructure;

/// <summary>
/// Boots the real API in-memory. SQL Server is swapped for an in-memory SQLite database so the
/// suite runs anywhere without Docker; everything else (DI, middleware, EF mappings) is real.
/// </summary>
public sealed class NepalParcelApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Satisfies startup validation; the SQL Server registration is replaced below.
        builder.UseSetting("ConnectionStrings:DefaultConnection", "Server=unused;Database=unused");
        builder.UseSetting("Database:ApplyMigrationsOnStartup", "false");
        builder.UseSetting("Swagger:Enabled", "true");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");

        // A throwaway signing key generated per test run: nothing secret is stored in the repository.
        builder.UseSetting("Jwt:SigningKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)));
        builder.UseSetting("RateLimiting:AuthPermitPerMinute", "100000");
        builder.UseSetting("RateLimiting:BookingPermitPerMinute", "100000");
        builder.UseSetting("RateLimiting:TrackingPermitPerMinute", "100000");
        builder.UseSetting("RateLimiting:TrackingOtpPermitPerMinute", "100000");
        builder.UseSetting("Booking:Receipt:CompanyName", "Afno Parcel Test Co");
        builder.UseSetting("Booking:Receipt:CompanyPhone", "01-4000000");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<NepalParcelDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<NepalParcelDbContext>>();

            services.AddDbContext<NepalParcelDbContext>((sp, options) => options
                .UseSqlite(_connection)
                .AddInterceptors(
                    sp.GetRequiredService<AuditableEntityInterceptor>(),
                    sp.GetRequiredService<ParcelHistoryGuardInterceptor>()));
        });
    }

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();

        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        await db.Database.EnsureCreatedAsync();

        // The same default service areas and development price list a fresh developer database gets.
        await ReferenceDataSeeder.SeedAsync(db, DateTimeOffset.UtcNow.AddDays(-1));
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
