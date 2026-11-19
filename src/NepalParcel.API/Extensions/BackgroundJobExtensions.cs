using System.ComponentModel.DataAnnotations;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.InMemory;
using Hangfire.SqlServer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NepalParcel.Application.Notifications;
using NepalParcel.Infrastructure.Persistence;

namespace NepalParcel.API.Extensions;

/// <summary>Background job settings (configuration section <c>Hangfire</c>). Not secrets.</summary>
public sealed class HangfireSettings
{
    public const string SectionName = "Hangfire";
    public const string SqlServerMode = "SqlServer";
    public const string InMemoryMode = "InMemory";
    public const string DisabledMode = "Disabled";

    /// <summary>
    /// <c>SqlServer</c> keeps Hangfire's own tables (schema <c>hangfire</c>) in the application database: use it in production.
    /// <c>InMemory</c> keeps them in memory, lost on restart (the outbox is in the database, so no message is lost: the next
    /// sweep finds it). <c>Disabled</c> runs no worker at all: nothing is sent (tests).
    /// </summary>
    [Required]
    [RegularExpression("^(SqlServer|InMemory|Disabled)$", ErrorMessage = "Hangfire:Mode must be SqlServer, InMemory or Disabled.")]
    public string Mode { get; set; } = SqlServerMode;

    [Range(1, 32)]
    public int WorkerCount { get; set; } = 2;

    /// <summary>How often the worker looks at the outbox on its own (a cron expression, default every minute).</summary>
    [Required]
    public string SweepCron { get; set; } = "* * * * *";

    /// <summary>
    /// Serves Hangfire's dashboard at <c>/hangfire</c>, <b>only to requests from the same machine</b>. Off by default. Never
    /// expose it publicly: it shows job arguments and can re-run jobs.
    /// </summary>
    public bool DashboardEnabled { get; set; }

    public bool IsDisabled => string.Equals(Mode, DisabledMode, StringComparison.Ordinal);
}

/// <summary>What the background worker runs. Hangfire creates one per job with the application's scoped services.</summary>
public sealed class NotificationJobs(IOutboxProcessor processor, INotificationWebhookService webhooks)
{
    public const string SweepJobId = "notification-outbox";

    /// <summary>
    /// Tries again the delivery reports that could not be matched yet, then sends what is due. Safe to run at the same time as
    /// itself: a message is claimed by one worker at a time.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        await webhooks.RetryPendingAsync(cancellationToken);
        await processor.ProcessDueAsync(cancellationToken);
    }
}

/// <summary>Asks the worker to sweep right after a message was queued, so it goes out within moments.</summary>
internal sealed partial class HangfireOutboxDispatchSignal(IBackgroundJobClient jobs, ILogger<HangfireOutboxDispatchSignal> logger) : IOutboxDispatchSignal
{
    private long _lastTicks;

    public void Signal()
    {
        // A burst of saves asks once. A signal that is skipped or lost only delays a message until the next scheduled sweep.
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastTicks);
        if (now - last < 1000 || Interlocked.CompareExchange(ref _lastTicks, now, last) != last)
        {
            return;
        }

        try
        {
            jobs.Enqueue<NotificationJobs>(job => job.SweepAsync(CancellationToken.None));
        }
#pragma warning disable CA1031 // A failed hint must never fail the business change that has already been saved.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogSignalFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not ask the background worker to send notifications now; the scheduled sweep will do it.")]
    private static partial void LogSignalFailed(ILogger logger, Exception exception);
}

internal sealed class LoopbackOnlyDashboardFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var address = context.GetHttpContext().Connection.RemoteIpAddress;
        return address is not null && System.Net.IPAddress.IsLoopback(address);
    }
}

internal static partial class BackgroundJobExtensions
{
    public static IServiceCollection AddBackgroundJobs(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<HangfireSettings>()
            .Bind(configuration.GetSection(HangfireSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var settings = configuration.GetSection(HangfireSettings.SectionName).Get<HangfireSettings>() ?? new HangfireSettings();
        if (settings.IsDisabled)
        {
            return services;
        }

        // The storage is built when the worker first needs it, which is after the database has been created and migrated.
        services.AddHangfire((sp, config) =>
        {
            config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings();

            if (string.Equals(settings.Mode, HangfireSettings.InMemoryMode, StringComparison.Ordinal))
            {
                config.UseInMemoryStorage();
            }
            else
            {
                var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString(DatabaseOptions.ConnectionStringName)
                    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required for Hangfire:Mode SqlServer.");
                config.UseSqlServerStorage(connectionString, new SqlServerStorageOptions
                {
                    SchemaName = "hangfire",
                    PrepareSchemaIfNecessary = true,
                    CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
                    SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
                    QueuePollInterval = TimeSpan.FromSeconds(5),
                    UseRecommendedIsolationLevel = true,
                    DisableGlobalLocks = true,
                });
            }
        });
        services.AddHangfireServer(options =>
        {
            options.WorkerCount = settings.WorkerCount;
            options.ServerName = $"afno-parcel-{Environment.MachineName}";
        });

        services.AddScoped<NotificationJobs>();
        services.Replace(ServiceDescriptor.Singleton<IOutboxDispatchSignal, HangfireOutboxDispatchSignal>());
        return services;
    }

    /// <summary>Schedules the recurring sweep and (when switched on) serves the loopback-only dashboard.</summary>
    public static WebApplication UseBackgroundJobs(this WebApplication app)
    {
        var settings = app.Configuration.GetSection(HangfireSettings.SectionName).Get<HangfireSettings>() ?? new HangfireSettings();
        if (settings.IsDisabled)
        {
            return app;
        }

        if (settings.DashboardEnabled)
        {
            app.UseHangfireDashboard("/hangfire", new DashboardOptions
            {
                Authorization = [new LoopbackOnlyDashboardFilter()],
                DashboardTitle = "Afno Parcel jobs",
                IsReadOnlyFunc = _ => true,
            });
        }

        return app;
    }

    /// <summary>Registers the recurring sweep. Run after the database exists: Hangfire's storage is opened here.</summary>
    public static void ScheduleRecurringJobs(this WebApplication app)
    {
        var settings = app.Configuration.GetSection(HangfireSettings.SectionName).Get<HangfireSettings>() ?? new HangfireSettings();
        if (settings.IsDisabled)
        {
            return;
        }

        app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<NotificationJobs>(
            NotificationJobs.SweepJobId,
            job => job.SweepAsync(CancellationToken.None),
            settings.SweepCron);
    }

    /// <summary>Says plainly at startup when messages are not really being sent.</summary>
    public static async Task LogNotificationProvidersAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Notifications");
        var settings = app.Configuration.GetSection(HangfireSettings.SectionName).Get<HangfireSettings>() ?? new HangfireSettings();
        var providers = scope.ServiceProvider.GetRequiredService<IMessageProviderRegistry>().Describe();
        foreach (var provider in providers)
        {
            if (provider.IsMock)
            {
                LogMock(logger, provider.Channel);
            }
            else if (!provider.IsConfigured)
            {
                LogNotConfigured(logger, provider.Channel, provider.Provider);
            }
        }

        if (settings.IsDisabled)
        {
            LogNoWorker(logger);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Channel} notifications use a MOCK provider: messages are kept in the test inbox and nothing is sent to any phone.")]
    private static partial void LogMock(ILogger logger, string channel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The {Channel} provider '{Provider}' is missing its credentials, so {Channel} messages will fail until they are set.")]
    private static partial void LogNotConfigured(ILogger logger, string channel, string provider);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hangfire is disabled: no worker is running, so queued notifications will not be sent.")]
    private static partial void LogNoWorker(ILogger logger);
}
