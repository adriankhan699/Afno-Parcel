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
    public const string BranchCreated = "BranchCreated";
    public const string BranchUpdated = "BranchUpdated";
    public const string BranchActivated = "BranchActivated";
    public const string BranchDeactivated = "BranchDeactivated";
    public const string WarehouseCreated = "WarehouseCreated";
    public const string WarehouseUpdated = "WarehouseUpdated";
    public const string WarehouseActivated = "WarehouseActivated";
    public const string WarehouseDeactivated = "WarehouseDeactivated";
    public const string StaffAssigned = "StaffAssigned";
    public const string StaffUnassigned = "StaffUnassigned";
    public const string TrackingOtpRequested = "TrackingOtpRequested";
    public const string TrackingOtpVerified = "TrackingOtpVerified";
    public const string PaymentConfirmed = "PaymentConfirmed";
    public const string PricingRuleCreated = "PricingRuleCreated";
    public const string PricingRuleDeactivated = "PricingRuleDeactivated";
    public const string PaymentInitiated = "PaymentInitiated";
    public const string PaymentVerified = "PaymentVerified";
    public const string PaymentFailed = "PaymentFailed";
    public const string PaymentVerificationMismatch = "PaymentMismatch";
    public const string PaymentDuplicateCharge = "PaymentDuplicate";
    public const string NotificationRequeued = "NotificationRequeued";
    public const string NotificationCancelled = "NotificationCancelled";
    public const string NotificationTemplateUpdated = "NotificationTemplate";
    public const string NotificationOptOutAdded = "NotificationOptOut";
    public const string NotificationOptOutRemoved = "NotificationOptIn";
    public const string NotificationPreferencesChanged = "NotificationPrefs";
    public const string OfficeCashRecorded = "OfficeCashRecorded";
    public const string SettlementSubmitted = "SettlementSubmitted";
    public const string SettlementVerified = "SettlementVerified";
    public const string SettlementDisputed = "SettlementDisputed";
    public const string SettlementResolved = "SettlementResolved";
    public const string AgentCreated = "AgentCreated";
    public const string AgentUpdated = "AgentUpdated";
    public const string AgentActivated = "AgentActivated";
    public const string AgentDeactivated = "AgentDeactivated";
    public const string DeliveryAssigned = "DeliveryAssigned";
    public const string DeliveryAssignmentCancelled = "DeliveryAssignmentCancelled";
    public const string DeliveryCodeSent = "DeliveryCodeSent";
    public const string DeliveryProofRecorded = "DeliveryProofRecorded";
    public const string CodCollected = "CodCollected";
    public const string ParcelReturnedToWarehouse = "ParcelReturnedToWarehouse";
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
