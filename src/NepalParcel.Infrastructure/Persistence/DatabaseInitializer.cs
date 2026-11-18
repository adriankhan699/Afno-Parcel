using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NepalParcel.Infrastructure.Persistence;

public static partial class DatabaseInitializer
{
    /// <summary>
    /// Applies pending migrations when <see cref="DatabaseOptions.ApplyMigrationsOnStartup"/> is enabled.
    /// Production deployments should instead run a migration bundle / script as a separate release step.
    /// </summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        if (!options.ApplyMigrationsOnStartup)
        {
            return;
        }

        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseInitializer));
        var dbContext = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();

        var pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
        if (pending.Count == 0)
        {
            LogUpToDate(logger);
            return;
        }

        LogApplying(logger, pending.Count, string.Join(", ", pending));
        await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        LogApplied(logger);
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Database schema is up to date")]
    private static partial void LogUpToDate(ILogger logger);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Information, Message = "Applying {Count} pending migration(s): {Migrations}")]
    private static partial void LogApplying(ILogger logger, int count, string migrations);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Information, Message = "Database migrations applied")]
    private static partial void LogApplied(ILogger logger);
}
