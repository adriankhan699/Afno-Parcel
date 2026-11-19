using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NepalParcel.Application.Notifications;
using NepalParcel.Domain.Notifications;
using NepalParcel.Infrastructure.Persistence;

namespace NepalParcel.Infrastructure.Notifications;

/// <summary>Does nothing. Used when no background worker is running (tests, or Hangfire switched off).</summary>
internal sealed class NoOpOutboxDispatchSignal : IOutboxDispatchSignal
{
    public void Signal()
    {
    }
}

/// <summary>
/// Tells the background worker to look at the outbox <b>after</b> a save that added outbox rows has committed, so a message is
/// sent within moments instead of at the next scheduled sweep. It never decides anything: if the signal is lost, the
/// scheduled sweep still finds the row. Nothing is signalled for a save that failed or was rolled back.
/// </summary>
internal sealed class OutboxDispatchInterceptor(IOutboxDispatchSignal signal) : SaveChangesInterceptor
{
    private bool _hasNewMessages;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Note(eventData);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Note(eventData);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Flush();
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Flush();
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => _hasNewMessages = false;

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _hasNewMessages = false;
        return Task.CompletedTask;
    }

    private void Note(DbContextEventData eventData)
    {
        if (eventData.Context is { } context)
        {
            _hasNewMessages = context.ChangeTracker
                .Entries<NotificationOutboxMessage>()
                .Any(e => e.State is EntityState.Added or EntityState.Modified && e.Entity.Status == OutboxStatus.Pending);
        }
    }

    private void Flush()
    {
        if (!_hasNewMessages)
        {
            return;
        }

        _hasNewMessages = false;
        signal.Signal();
    }
}

/// <summary>
/// Writes the default message texts the first time the system runs, and adds any default that is missing later (a new message
/// type in a later version) without ever overwriting a text an administrator has edited. This is not sample data: without
/// these rows nothing can be sent, so it runs in every environment.
/// </summary>
public static partial class NotificationTemplateSeeder
{
    public static async Task SeedNotificationTemplatesAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        if (!scope.ServiceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value.SeedNotificationTemplates)
        {
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(NotificationTemplateSeeder));

        var added = await SeedAsync(db, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (added > 0)
        {
            LogSeeded(logger, added);
        }
    }

    /// <summary>Adds the missing default templates and returns how many were added.</summary>
    public static async Task<int> SeedAsync(NepalParcelDbContext db, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var existing = await db.NotificationTemplates
            .AsNoTracking()
            .Select(t => new { t.Key, t.Channel, t.Language })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var added = 0;
        foreach (var seed in DefaultTemplates.All)
        {
            if (existing.Any(t => t.Key == seed.Key && t.Channel == seed.Channel && t.Language == NotificationOutboxMessage.DefaultLanguage))
            {
                continue;
            }

            db.NotificationTemplates.Add(NotificationTemplate.Create(
                seed.Key, seed.Channel, NotificationOutboxMessage.DefaultLanguage, seed.Body, seed.ProviderTemplateName, nowUtc));
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return added;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Added {Count} default notification templates.")]
    private static partial void LogSeeded(ILogger logger, int count);
}
