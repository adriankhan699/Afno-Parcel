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

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<NepalParcelDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<NepalParcelDbContext>>();

            services.AddDbContext<NepalParcelDbContext>((sp, options) => options
                .UseSqlite(_connection)
                .AddInterceptors(sp.GetRequiredService<AuditableEntityInterceptor>()));
        });
    }

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();

        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
