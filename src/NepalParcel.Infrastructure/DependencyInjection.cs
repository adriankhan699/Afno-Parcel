using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Branches;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Delivery;
using NepalParcel.Application.Network;
using NepalParcel.Application.Payments;
using NepalParcel.Application.Notifications;
using NepalParcel.Application.Parcels;
using NepalParcel.Application.Tracking;
using NepalParcel.Infrastructure.Identity;
using NepalParcel.Infrastructure.Labels;
using NepalParcel.Infrastructure.Notifications;
using NepalParcel.Infrastructure.Persistence;
using NepalParcel.Infrastructure.Persistence.Interceptors;
using NepalParcel.Infrastructure.Persistence.Repositories;
using NepalParcel.Infrastructure.Storage;

namespace NepalParcel.Infrastructure;

public static class DependencyInjection
{
    public const string DatabaseHealthCheckName = "database";

    /// <summary>Registers persistence (EF Core + SQL Server), repositories and infrastructure services.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<DatabaseOptions>()
            .BindConfiguration(DatabaseOptions.SectionName)
            .Configure<IConfiguration>((options, configuration) =>
                options.ConnectionString = configuration.GetConnectionString(DatabaseOptions.ConnectionStringName) ?? string.Empty)
            .ValidateDataAnnotations()
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.ConnectionString),
                $"ConnectionStrings:{DatabaseOptions.ConnectionStringName} is not configured. " +
                "Set it with 'dotnet user-secrets' or the ConnectionStrings__DefaultConnection environment variable.")
            .ValidateOnStart();

        services.TryAddSingleton<IOutboxDispatchSignal, NoOpOutboxDispatchSignal>();
        services.AddScoped<OutboxDispatchInterceptor>();
        services.AddScoped<AuditableEntityInterceptor>();
        services.AddSingleton<ParcelHistoryGuardInterceptor>();

        services.AddDbContext<NepalParcelDbContext>((sp, options) =>
        {
            var db = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;

            options.UseSqlServer(db.ConnectionString, sql =>
            {
                sql.MigrationsHistoryTable("__EFMigrationsHistory", NepalParcelDbContext.DefaultSchema);
                sql.CommandTimeout(db.CommandTimeoutSeconds);
                if (db.MaxRetryCount > 0)
                {
                    sql.EnableRetryOnFailure(db.MaxRetryCount);
                }
            });

            options.AddInterceptors(
                sp.GetRequiredService<AuditableEntityInterceptor>(),
                sp.GetRequiredService<ParcelHistoryGuardInterceptor>(),
                sp.GetRequiredService<OutboxDispatchInterceptor>());

            if (db.EnableSensitiveDataLogging)
            {
                options.EnableSensitiveDataLogging();
            }
        });

        services.AddScoped<IUnitOfWork, UnitOfWork>();

        AddIdentity(services);
        services.AddOptions<BootstrapSuperAdminOptions>().BindConfiguration(BootstrapSuperAdminOptions.SectionName);

        // Module repositories.
        services.AddScoped<IBranchRepository, BranchRepository>();
        services.AddScoped<IParcelRepository, ParcelRepository>();
        services.AddScoped<IPaymentRepository, PaymentRepository>();
        services.AddScoped<IPricingRepository, PricingRepository>();
        services.AddScoped<IWarehouseRepository, WarehouseRepository>();
        services.AddScoped<IStaffAssignmentRepository, StaffAssignmentRepository>();
        services.AddScoped<IMovementRepository, MovementRepository>();
        services.AddScoped<IInventoryRepository, InventoryRepository>();
        services.AddSingleton<ILabelGenerator, ZXingLabelGenerator>();
        services.AddScoped<IDeliveryAgentRepository, DeliveryAgentRepository>();
        services.AddScoped<IDeliveryAssignmentRepository, DeliveryAssignmentRepository>();
        services.AddScoped<IDeliveryProofRepository, DeliveryProofRepository>();
        services.AddScoped<ICodCollectionRepository, CodCollectionRepository>();
        services.AddScoped<IDeliveryOtpRepository, DeliveryOtpRepository>();
        services.AddScoped<IPaymentTransactionRepository, PaymentTransactionRepository>();
        services.AddScoped<IPaymentReportRepository, PaymentReportRepository>();
        services.AddScoped<ICodSettlementRepository, CodSettlementRepository>();
        services.AddScoped<ITestGatewaySessions, TestGatewaySessionRepository>();
        services.AddSingleton<IProofStorage, LocalProofStorage>();
        services.AddScoped<ITrackingOtpRepository, TrackingOtpRepository>();
        services.AddScoped<INotificationOutboxRepository, NotificationOutboxRepository>();
        services.AddScoped<INotificationTemplateRepository, NotificationTemplateRepository>();
        services.AddScoped<INotificationOptOutRepository, NotificationOptOutRepository>();
        services.AddScoped<INotificationConsentRepository, NotificationConsentRepository>();
        services.AddScoped<IWebhookEventRepository, WebhookEventRepository>();
        services.AddScoped<IMockProviderMessageRepository, MockProviderMessageRepository>();

        // The real WhatsApp provider is always available to choose (Notifications:WhatsApp:Provider = Meta); the mock is the default.
        services.AddHttpClient<MetaWhatsAppCloudProvider>((sp, client) =>
            client.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<NotificationOptions>>().Value.WhatsApp.TimeoutSeconds));
        services.AddTransient<IMessageProvider>(sp => sp.GetRequiredService<MetaWhatsAppCloudProvider>());
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();
        services.AddScoped<IIdentityService, IdentityService>();

        services.AddHealthChecks()
            .AddDbContextCheck<NepalParcelDbContext>(DatabaseHealthCheckName, tags: ["ready"]);

        return services;
    }

    private static void AddIdentity(IServiceCollection services)
    {
        services
            .AddIdentityCore<ApplicationUser>(options =>
            {
                // Passwords: long enough to resist guessing, mixed case + digit. (Hashing is PBKDF2 via Identity's hasher.)
                options.Password.RequiredLength = 10;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredUniqueChars = 4;

                options.User.RequireUniqueEmail = true;

                // Slow down online password guessing.
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<NepalParcelDbContext>();
    }
}
