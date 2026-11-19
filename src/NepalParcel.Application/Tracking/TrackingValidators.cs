using FluentValidation;
using NepalParcel.Application.Common.Validation;
using NepalParcel.Application.Parcels;
using NepalParcel.Contracts.Tracking;
using NepalParcel.Contracts.Warehouses;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Warehouses;

namespace NepalParcel.Application.Tracking;

/// <summary>The kinds of warehouse scan and the parcel status each one produces.</summary>
public enum ScanType
{
    /// <summary>The parcel arrives at the warehouse.</summary>
    Arrival = 1,

    /// <summary>The parcel leaves the warehouse for the next hub.</summary>
    Dispatch = 2,

    /// <summary>The parcel is handed to a rider for delivery.</summary>
    OutForDelivery = 3,
}

internal static class ScanTypeExtensions
{
    public static ParcelStatusCode ToStatus(this ScanType scanType) => scanType switch
    {
        ScanType.Arrival => ParcelStatusCode.ArrivedAtWarehouse,
        ScanType.Dispatch => ParcelStatusCode.InTransit,
        ScanType.OutForDelivery => ParcelStatusCode.OutForDelivery,
        _ => throw new DomainException($"'{scanType}' is not a valid scan type."),
    };
}

public sealed class UpdateParcelStatusRequestValidator : AbstractValidator<UpdateParcelStatusRequest>
{
    public UpdateParcelStatusRequestValidator()
    {
        var settable = ParcelStatusPermissions.Settable.Select(s => s.ToString()).ToArray();

        RuleFor(r => r.Status)
            .NotEmpty()
            .Must(s => ParcelRuleExtensions.IsEnumName<ParcelStatusCode>(s) &&
                       settable.Contains(Enum.Parse<ParcelStatusCode>(s, ignoreCase: true).ToString(), StringComparer.Ordinal))
            .WithMessage($"Status must be one of: {string.Join(", ", settable)}. To cancel a booking use the cancel endpoint.");

        RuleFor(r => r.Remarks).MaximumLength(ParcelStatusRules.RemarksMaxLength);
        RuleFor(r => r.BranchId).NotEqual(Guid.Empty).When(r => r.BranchId.HasValue);
        RuleFor(r => r.WarehouseId).NotEqual(Guid.Empty).When(r => r.WarehouseId.HasValue);
    }
}

public sealed class ScanRequestValidator : AbstractValidator<ScanRequest>
{
    public const int TrackingNumberInputMaxLength = 40;

    public ScanRequestValidator()
    {
        RuleFor(r => r.TrackingNumber).NotEmpty().MaximumLength(TrackingNumberInputMaxLength);
        RuleFor(r => r.ScanType).MustBeEnumName<ScanRequest, ScanType>("Scan type");
        RuleFor(r => r.WarehouseId).NotEmpty();
        RuleFor(r => r.Remarks).MaximumLength(ParcelStatusRules.RemarksMaxLength);
    }
}

public sealed class BatchScanRequestValidator : AbstractValidator<BatchScanRequest>
{
    public const int MaxBatchSize = 100;

    public BatchScanRequestValidator()
    {
        RuleFor(r => r.ScanType).MustBeEnumName<BatchScanRequest, ScanType>("Scan type");
        RuleFor(r => r.WarehouseId).NotEmpty();
        RuleFor(r => r.Remarks).MaximumLength(ParcelStatusRules.RemarksMaxLength);
        RuleFor(r => r.TrackingNumbers)
            .NotNull()
            .Must(t => t is { Count: >= 1 and <= MaxBatchSize })
            .WithMessage($"Send between 1 and {MaxBatchSize} tracking numbers.");
        RuleForEach(r => r.TrackingNumbers)
            .NotEmpty()
            .MaximumLength(ScanRequestValidator.TrackingNumberInputMaxLength);
    }
}

public sealed class VerifyTrackingOtpRequestValidator : AbstractValidator<VerifyTrackingOtpRequest>
{
    public VerifyTrackingOtpRequestValidator()
    {
        RuleFor(r => r.Code)
            .NotEmpty()
            .Matches(@"^\d{6}$")
            .WithMessage("Enter the 6-digit code.");
    }
}

public sealed class CreateWarehouseRequestValidator : AbstractValidator<CreateWarehouseRequest>
{
    public CreateWarehouseRequestValidator()
    {
        RuleFor(r => r.BranchId).NotEmpty();
        RuleFor(r => r.Code)
            .NotEmpty()
            .MaximumLength(Warehouse.CodeMaxLength)
            .Matches("^[A-Za-z0-9]+$").WithMessage("Code may contain only letters and digits.");
        RuleFor(r => r.Name).NotEmpty().MaximumLength(Warehouse.NameMaxLength);
        RuleFor(r => r.Address).NotNull().SetValidator(new AddressDtoValidator());
        RuleFor(r => r.PhoneNumber)
            .NotEmpty()
            .MaximumLength(NepalPhoneNumber.MaxLength)
            .Must(NepalPhoneNumber.IsValid)
            .WithMessage("Phone number must be a valid Nepali mobile (98XXXXXXXX) or landline (01-XXXXXXX) number.");
        RuleFor(r => r.CapacityParcels).GreaterThan(0).When(r => r.CapacityParcels.HasValue);
    }
}
