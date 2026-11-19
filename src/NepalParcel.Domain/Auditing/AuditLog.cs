using NepalParcel.Domain.Common;

namespace NepalParcel.Domain.Auditing;

/// <summary>Well-known audit actions. Free-form values are also allowed.</summary>
public static class AuditActions
{
    public const string Register = "Register";
    public const string Login = "Login";
    public const string LoginFailed = "LoginFailed";
    public const string Logout = "Logout";
    public const string TokenRefreshed = "TokenRefreshed";
    public const string TokenReuseDetected = "TokenReuseDetected";
    public const string UserCreated = "UserCreated";
    public const string Create = "Create";
    public const string Update = "Update";
    public const string Delete = "Delete";
    public const string ParcelBooked = "ParcelBooked";
    public const string ParcelCancelled = "ParcelCancelled";
    public const string ParcelStatusChanged = "ParcelStatusChanged";
    public const string TrackingOtpRequested = "TrackingOtpRequested";
    public const string TrackingOtpVerified = "TrackingOtpVerified";
    public const string PaymentConfirmed = "PaymentConfirmed";
    public const string PricingRuleCreated = "PricingRuleCreated";
    public const string PricingRuleDeactivated = "PricingRuleDeactivated";
}

/// <summary>
/// Append-only record of who did what. Deliberately has no foreign keys so history survives
/// deletion of the referenced rows. Never store passwords, tokens or full personal data in the value columns.
/// </summary>
public sealed class AuditLog : Entity
{
    private AuditLog()
    {
        Action = string.Empty;
        EntityName = string.Empty;
    }

    public DateTimeOffset OccurredAtUtc { get; private init; }

    public Guid? UserId { get; private init; }

    public string Action { get; private init; }

    public string EntityName { get; private init; }

    public string? EntityId { get; private init; }

    public string? OldValues { get; private init; }

    public string? NewValues { get; private init; }

    public string? IpAddress { get; private init; }

    public string? UserAgent { get; private init; }

    public string? CorrelationId { get; private init; }

    public static AuditLog Create(
        string action,
        string entityName,
        string? entityId,
        Guid? userId,
        DateTimeOffset occurredAtUtc,
        string? oldValues = null,
        string? newValues = null,
        string? ipAddress = null,
        string? userAgent = null,
        string? correlationId = null) =>
        new()
        {
            Action = Guard.NotBlank(action, "Action", 50),
            EntityName = Guard.NotBlank(entityName, "Entity name", 100),
            EntityId = Guard.Optional(entityId, "Entity id", 100),
            UserId = userId,
            OccurredAtUtc = occurredAtUtc,
            OldValues = oldValues,
            NewValues = newValues,
            IpAddress = Guard.Optional(ipAddress, "IP address", 64),
            UserAgent = userAgent is { Length: > 256 } ? userAgent[..256] : userAgent,
            CorrelationId = Guard.Optional(correlationId, "Correlation id", 64),
        };
}
