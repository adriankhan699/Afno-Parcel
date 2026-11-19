using System.ComponentModel.DataAnnotations;

namespace NepalParcel.API.Options;

/// <summary>
/// JWT settings (section <c>Jwt</c>). <see cref="SigningKey"/> is a secret: supply it through user-secrets or the
/// <c>Jwt__SigningKey</c> environment variable — never commit it. The application refuses to start without a strong key.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public const int MinSigningKeyLength = 32;

    [Required]
    public string Issuer { get; set; } = string.Empty;

    [Required]
    public string Audience { get; set; } = string.Empty;

    [Required]
    [MinLength(MinSigningKeyLength, ErrorMessage = "Jwt:SigningKey must be at least 32 characters (use a random 64+ character value).")]
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>Access token lifetime. Keep short; refresh tokens provide continuity.</summary>
    [Range(1, 120)]
    public int AccessTokenMinutes { get; set; } = 15;

    [Range(1, 90)]
    public int RefreshTokenDays { get; set; } = 7;
}

/// <summary>Rate limiting for anonymous authentication endpoints (section <c>RateLimiting</c>).</summary>
public sealed class RateLimitSettings
{
    public const string SectionName = "RateLimiting";
    public const string AuthPolicy = "auth";
    public const string BookingPolicy = "booking";
    public const string TrackingPolicy = "tracking";
    public const string TrackingOtpPolicy = "tracking-otp";
    public const string WebhookPolicy = "webhook";

    /// <summary>Requests per client IP per minute to register/login/refresh/logout.</summary>
    [Range(1, 100000)]
    public int AuthPermitPerMinute { get; set; } = 10;

    /// <summary>Price quotes, bookings, cancellations and payment confirmations per signed-in user (or client IP when anonymous) per minute.</summary>
    [Range(1, 100000)]
    public int BookingPermitPerMinute { get; set; } = 30;

    /// <summary>Public tracking lookups per client IP per minute.</summary>
    [Range(1, 100000)]
    public int TrackingPermitPerMinute { get; set; } = 60;

    /// <summary>One-time code requests and verifications per client IP per minute.</summary>
    [Range(1, 100000)]
    public int TrackingOtpPermitPerMinute { get; set; } = 10;

    /// <summary>Provider webhook calls per client IP per minute. Higher than the others: a provider reports every message.</summary>
    [Range(1, 100000)]
    public int WebhookPermitPerMinute { get; set; } = 600;
}
