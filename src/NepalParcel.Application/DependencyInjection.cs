using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Branches;
using NepalParcel.Application.Parcels;
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

        // Modules: register each module's services here as they are added (Tracking, Payments...).
        services.AddScoped<IBranchService, BranchService>();
        services.AddScoped<PriceEngine>();
        services.AddScoped<IPricingService, PricingService>();
        services.AddScoped<IParcelService, ParcelService>();
        services.AddScoped<IParcelStatusService, ParcelStatusService>();
        services.AddScoped<ITrackingService, TrackingService>();
        services.AddScoped<IWarehouseService, WarehouseService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserAdministrationService, UserAdministrationService>();

        return services;
    }
}
