using FluentValidation;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Common.Validation;
using NepalParcel.Application.Parcels;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Delivery;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Delivery;

namespace NepalParcel.Application.Delivery;

internal static class DeliveryRules
{
    public const int ReasonMinLength = 5;
    public const int ReasonMaxLength = 200;
    public const int RemarksMaxLength = 250;
    public const int MaxReportDays = 366;

    public const string OpenScope = "open";
    public const string HistoryScope = "history";
    public const string OpenStatus = "Open";

    public static readonly AssignmentStatus[] OpenStatuses = [AssignmentStatus.Assigned, AssignmentStatus.Accepted, AssignmentStatus.InProgress];
}

public sealed class CreateDeliveryAgentRequestValidator : AbstractValidator<CreateDeliveryAgentRequest>
{
    public CreateDeliveryAgentRequestValidator()
    {
        RuleFor(r => r.FullName).NotEmpty().MaximumLength(DeliveryAgent.NameMaxLength);
        RuleFor(r => r.Email).ValidEmail();
        RuleFor(r => r.PhoneNumber).NotEmpty().Must(NepalPhoneNumber.IsValid)
            .WithMessage("Phone number must be a valid Nepali mobile (98XXXXXXXX) or landline (01-XXXXXXX) number.");
        RuleFor(r => r.Password).ValidNewPassword();
        RuleFor(r => r.BranchId).NotEmpty();
        RuleFor(r => r.VehicleType).MaximumLength(40);
        RuleFor(r => r.VehicleNumber).MaximumLength(30);
        RuleFor(r => r.LicenseNumber).MaximumLength(40);
    }
}

public sealed class UpdateDeliveryAgentRequestValidator : AbstractValidator<UpdateDeliveryAgentRequest>
{
    public UpdateDeliveryAgentRequestValidator()
    {
        RuleFor(r => r.PhoneNumber).NotEmpty().Must(NepalPhoneNumber.IsValid)
            .WithMessage("Phone number must be a valid Nepali mobile (98XXXXXXXX) or landline (01-XXXXXXX) number.");
        RuleFor(r => r.VehicleType).MaximumLength(40);
        RuleFor(r => r.VehicleNumber).MaximumLength(30);
        RuleFor(r => r.LicenseNumber).MaximumLength(40);
    }
}

public sealed class DeliveryAgentQueryValidator : AbstractValidator<DeliveryAgentQuery>
{
    public DeliveryAgentQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, PageQuery.MaxPageSize);
        RuleFor(q => q.Search).MaximumLength(100);
    }
}

public sealed class AssignDeliveryRequestValidator : AbstractValidator<AssignDeliveryRequest>
{
    public AssignDeliveryRequestValidator()
    {
        RuleFor(r => r.ParcelId).NotEmpty();
        RuleFor(r => r.DeliveryAgentId).NotEmpty();
    }
}

public sealed class CancelAssignmentRequestValidator : AbstractValidator<CancelAssignmentRequest>
{
    public CancelAssignmentRequestValidator()
    {
        RuleFor(r => r.Reason).NotEmpty().MinimumLength(DeliveryRules.ReasonMinLength).MaximumLength(DeliveryRules.ReasonMaxLength);
    }
}

public sealed class DeliveryAssignmentQueryValidator : AbstractValidator<DeliveryAssignmentQuery>
{
    public DeliveryAssignmentQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, PageQuery.MaxPageSize);
        RuleFor(q => q.Status!)
            .Must(s => string.Equals(s, DeliveryRules.OpenStatus, StringComparison.OrdinalIgnoreCase) || ParcelRuleExtensions.IsEnumName<AssignmentStatus>(s))
            .When(q => !string.IsNullOrWhiteSpace(q.Status))
            .WithMessage($"Status must be Open or one of: {string.Join(", ", Enum.GetNames<AssignmentStatus>())}.")
            .OverridePropertyName(nameof(DeliveryAssignmentQuery.Status));
        RuleFor(q => q.To)
            .GreaterThanOrEqualTo(q => q.From!.Value)
            .When(q => q.From.HasValue && q.To.HasValue)
            .WithMessage("The end date cannot be before the start date.");
    }
}

public sealed class ReadyParcelQueryValidator : AbstractValidator<ReadyParcelQuery>
{
    public ReadyParcelQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, PageQuery.MaxPageSize);
        RuleFor(q => q.Search).MaximumLength(100);
    }
}

public sealed class AgentAssignmentQueryValidator : AbstractValidator<AgentAssignmentQuery>
{
    public AgentAssignmentQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, PageQuery.MaxPageSize);
        RuleFor(q => q.Scope!)
            .Must(s => string.Equals(s, DeliveryRules.OpenScope, StringComparison.OrdinalIgnoreCase) || string.Equals(s, DeliveryRules.HistoryScope, StringComparison.OrdinalIgnoreCase))
            .When(q => !string.IsNullOrWhiteSpace(q.Scope))
            .WithMessage("Scope must be 'open' or 'history'.")
            .OverridePropertyName(nameof(AgentAssignmentQuery.Scope));
    }
}

public sealed class MarkDeliveryFailedRequestValidator : AbstractValidator<MarkDeliveryFailedRequest>
{
    public MarkDeliveryFailedRequestValidator()
    {
        RuleFor(r => r.Reason).NotEmpty().MinimumLength(DeliveryRules.ReasonMinLength).MaximumLength(DeliveryRules.ReasonMaxLength);
        RuleFor(r => r.Remarks).MaximumLength(DeliveryRules.RemarksMaxLength);
    }
}

public sealed class CollectCodRequestValidator : AbstractValidator<CollectCodRequest>
{
    public CollectCodRequestValidator()
    {
        RuleFor(r => r.CollectedAmount).GreaterThan(0).PrecisionScale(18, 2, false).WithMessage("The amount can have at most 2 decimal places.");
    }
}

public sealed class VerifyDeliveryCodeRequestValidator : AbstractValidator<VerifyDeliveryCodeRequest>
{
    public VerifyDeliveryCodeRequestValidator()
    {
        RuleFor(r => r.Code).NotEmpty().Matches("^[0-9]{6}$").WithMessage("The code is 6 digits.");
        RuleFor(r => r.RecipientName).MaximumLength(150);
    }
}

public sealed class CompleteDeliveryRequestValidator : AbstractValidator<CompleteDeliveryRequest>
{
    public CompleteDeliveryRequestValidator()
    {
        RuleFor(r => r.Remarks).MaximumLength(DeliveryRules.RemarksMaxLength);
    }
}

public sealed class ReturnToWarehouseRequestValidator : AbstractValidator<ReturnToWarehouseRequest>
{
    public ReturnToWarehouseRequestValidator()
    {
        RuleFor(r => r.WarehouseId).NotEmpty();
        RuleFor(r => r.Remarks).MaximumLength(DeliveryRules.RemarksMaxLength);
    }
}

public sealed class AgentPerformanceQueryValidator : AbstractValidator<AgentPerformanceQuery>
{
    public AgentPerformanceQueryValidator()
    {
        RuleFor(q => q.To)
            .GreaterThanOrEqualTo(q => q.From!.Value)
            .When(q => q.From.HasValue && q.To.HasValue)
            .WithMessage("The end date cannot be before the start date.");
        RuleFor(q => q)
            .Must(q => q.From is null || q.To is null || q.To.Value.DayNumber - q.From.Value.DayNumber < DeliveryRules.MaxReportDays)
            .WithMessage($"A report can cover at most {DeliveryRules.MaxReportDays} days.")
            .OverridePropertyName(nameof(AgentPerformanceQuery.To));
    }
}

/// <summary>Recognises the picture formats accepted as proof (by their first bytes, not by what the client claims).</summary>
internal static class ProofImage
{
    /// <summary>The file extension for PNG, JPEG or WebP content, or null for anything else.</summary>
    public static string? DetectExtension(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return "png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "jpg";
        }

        if (bytes.Length >= 12
            && bytes[..4].SequenceEqual("RIFF"u8)
            && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return "webp";
        }

        return null;
    }

    public static string ContentTypeFor(string storageKey) =>
        Path.GetExtension(storageKey).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "application/octet-stream",
        };
}
