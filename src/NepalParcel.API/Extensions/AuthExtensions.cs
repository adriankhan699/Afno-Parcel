using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NepalParcel.API.Options;
using NepalParcel.API.Security;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Tracking;
using NepalParcel.API.Services;

namespace NepalParcel.API.Extensions;

internal static class AuthExtensions
{
    /// <summary>JWT bearer authentication, role-based authorization, token service and rate limiting for the auth endpoints.</summary>
    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<RateLimitSettings>()
            .Bind(configuration.GetSection(RateLimitSettings.SectionName))
            .ValidateDataAnnotations();

        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddScoped<IClientInfo, HttpClientInfo>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<ITrackingSecurity, TrackingSecurity>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Configured through IOptions so values are read when first needed (works with user-secrets and test overrides).
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwtOptions) =>
            {
                var jwt = jwtOptions.Value;
                bearer.MapInboundClaims = false;
                bearer.RequireHttpsMetadata = false; // TLS is terminated by the host/reverse proxy; there is no metadata endpoint.
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "name",
                    RoleClaimType = "role",
                };
            });

        services.AddAuthorization();

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(RateLimitSettings.AuthPolicy, context =>
            {
                var permit = context.RequestServices.GetRequiredService<IOptions<RateLimitSettings>>().Value.AuthPermitPerMinute;
                return RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permit,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    });
            });

            limiter.AddPolicy(RateLimitSettings.TrackingPolicy, context =>
                PerIp(context, "tracking", c => c.RequestServices.GetRequiredService<IOptions<RateLimitSettings>>().Value.TrackingPermitPerMinute));
            limiter.AddPolicy(RateLimitSettings.WebhookPolicy, context =>
                PerIp(context, "webhook", c => c.RequestServices.GetRequiredService<IOptions<RateLimitSettings>>().Value.WebhookPermitPerMinute));
            limiter.AddPolicy(RateLimitSettings.TrackingOtpPolicy, context =>
                PerIp(context, "tracking-otp", c => c.RequestServices.GetRequiredService<IOptions<RateLimitSettings>>().Value.TrackingOtpPermitPerMinute));

            limiter.AddPolicy(RateLimitSettings.BookingPolicy, context =>
            {
                var permit = context.RequestServices.GetRequiredService<IOptions<RateLimitSettings>>().Value.BookingPermitPerMinute;
                // The user id cannot be read before authentication has run, so fall back to the client IP for anonymous quotes.
                var key = context.User.FindFirst("sub")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(
                    $"booking:{key}",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permit,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    });
            });
        });

        return services;
    }

    private static RateLimitPartition<string> PerIp(HttpContext context, string name, Func<HttpContext, int> permit) =>
        RateLimitPartition.GetFixedWindowLimiter(
            $"{name}:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permit(context),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
}
