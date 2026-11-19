using FluentValidation;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Branches;
using NepalParcel.Application.Delivery;
using NepalParcel.Application.Network;
using NepalParcel.Application.Notifications;
using NepalParcel.Application.Parcels;
using NepalParcel.Application.Payments;
using NepalParcel.Application.Tracking;

namespace NepalParcel.Application;

public static class DependencyInjection
{
    /// <summary>Registers application services (use cases) and validators.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, includeInternalTypes: true);

        services.AddOptions<BookingOptions>()
            .BindConfiguration(BookingOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<TrackingOptions>()
            .BindConfiguration(TrackingOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<DeliveryOptions>()
            .BindConfiguration(DeliveryOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<PaymentOptions>()
            .BindConfiguration(PaymentOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<NotificationOptions>()
            .BindConfiguration(NotificationOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<NotificationOptions>, NotificationOptionsValidator>();

        // Modules: register each module's services here as they are added (Tracking, Payments...).
        services.AddScoped<IBranchService, BranchService>();
        services.AddScoped<PriceEngine>();
        services.AddScoped<IPricingService, PricingService>();
        services.AddScoped<IParcelService, ParcelService>();
        services.AddScoped<IParcelStatusService, ParcelStatusService>();
        services.AddScoped<ITrackingService, TrackingService>();
        services.AddScoped<IWarehouseService, WarehouseService>();
        services.AddScoped<WorkplaceAccess>();
        services.AddScoped<ParcelStatusChanger>();
        services.AddScoped<IScanService, ScanService>();
        services.AddScoped<IInventoryService, InventoryService>();
        services.AddScoped<IStaffService, StaffService>();
        services.AddScoped<IParcelLabelService, ParcelLabelService>();
        services.AddScoped<PaymentLedger>();
        services.AddScoped<IPaymentGatewayRegistry, PaymentGatewayRegistry>();
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddScoped<ITestGatewayService, TestGatewayService>();
        services.AddScoped<ISettlementService, SettlementService>();
        services.AddScoped<IPaymentReportService, PaymentReportService>();
        services.AddScoped<AgentContext>();
        services.AddScoped<IDeliveryAgentService, DeliveryAgentService>();
        services.AddScoped<IDeliveryAssignmentService, DeliveryAssignmentService>();
        services.AddScoped<IAgentWorkService, AgentWorkService>();
        services.AddScoped<INotificationPlanner, NotificationPlanner>();
        services.AddScoped<IOutboxProcessor, OutboxProcessor>();
        services.AddScoped<INotificationWebhookService, NotificationWebhookService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<INotificationPreferenceService, NotificationPreferenceService>();
        services.AddScoped<IMessageProviderRegistry, MessageProviderRegistry>();
        services.AddScoped<IMessageProvider, MockWhatsAppProvider>();
        services.AddScoped<IMessageProvider, MockSmsProvider>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserAdministrationService, UserAdministrationService>();

        return services;
    }
}
