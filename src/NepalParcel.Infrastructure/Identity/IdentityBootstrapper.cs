using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NepalParcel.Application.Auth;
using NepalParcel.Domain.Identity;

namespace NepalParcel.Infrastructure.Identity;

/// <summary>
/// Optional first-run configuration for the initial SuperAdmin account. Nothing is created unless BOTH values are
/// supplied through secure configuration (user-secrets / environment variables) — no default credentials exist.
/// </summary>
public sealed class BootstrapSuperAdminOptions
{
    public const string SectionName = "Bootstrap:SuperAdmin";

    [EmailAddress]
    public string? Email { get; set; }

    public string? Password { get; set; }

    public string FullName { get; set; } = "Super Admin";

    public string? PhoneNumber { get; set; }
}

public static partial class IdentityBootstrapper
{
    /// <summary>Creates the SuperAdmin from configuration when none with that email exists. The password is never logged.</summary>
    public static async Task EnsureSuperAdminAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<BootstrapSuperAdminOptions>>().Value;
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(IdentityBootstrapper));

        if (string.IsNullOrWhiteSpace(options.Email) || string.IsNullOrWhiteSpace(options.Password))
        {
            LogNotConfigured(logger);
            return;
        }

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if (await users.FindByEmailAsync(options.Email.Trim()).ConfigureAwait(false) is not null)
        {
            return;
        }

        var identity = scope.ServiceProvider.GetRequiredService<IIdentityService>();
        var result = await identity
            .CreateUserAsync(options.FullName, options.Email, options.PhoneNumber, options.Password, Roles.SuperAdmin, cancellationToken)
            .ConfigureAwait(false);

        if (result.Succeeded)
        {
            LogCreated(logger, result.User!.Id);
        }
        else
        {
            LogFailed(logger, string.Join(", ", result.Failures.Select(f => f.Code)));
        }
    }

    [LoggerMessage(EventId = 2101, Level = LogLevel.Information, Message = "No SuperAdmin bootstrap configured (Bootstrap:SuperAdmin:Email/Password); skipping")]
    private static partial void LogNotConfigured(ILogger logger);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Information, Message = "SuperAdmin account {UserId} created from bootstrap configuration")]
    private static partial void LogCreated(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 2103, Level = LogLevel.Error, Message = "SuperAdmin bootstrap failed: {Failures}")]
    private static partial void LogFailed(ILogger logger, string failures);
}
