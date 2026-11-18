using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Branches;

namespace NepalParcel.Application;

public static class DependencyInjection
{
    /// <summary>Registers application services (use cases) and validators.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, includeInternalTypes: true);

        // Modules: register each module's services here as they are added (Parcels, Tracking, Pricing...).
        services.AddScoped<IBranchService, BranchService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserAdministrationService, UserAdministrationService>();

        return services;
    }
}
