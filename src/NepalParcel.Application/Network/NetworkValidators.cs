using FluentValidation;
using NepalParcel.Application.Common.Validation;
using NepalParcel.Application.Parcels;
using NepalParcel.Application.Tracking;
using NepalParcel.Contracts.Branches;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Staff;
using NepalParcel.Contracts.Tracking;
using NepalParcel.Contracts.Warehouses;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Warehouses;

namespace NepalParcel.Application.Network;

public sealed class UpdateBranchRequestValidator : AbstractValidator<UpdateBranchRequest>
{
    public UpdateBranchRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(Branch.NameMaxLength);
        RuleFor(r => r.Address).NotNull().SetValidator(new AddressDtoValidator());
        RuleFor(r => r.PhoneNumber)
            .NotEmpty()
            .MaximumLength(NepalPhoneNumber.MaxLength)
            .Must(NepalPhoneNumber.IsValid)
            .WithMessage("Phone number must be a valid Nepali mobile (98XXXXXXXX) or landline (01-XXXXXXX) number.");
        RuleFor(r => r.Email)
            .EmailAddress()
            .MaximumLength(Branch.EmailMaxLength)
            .When(r => !string.IsNullOrWhiteSpace(r.Email));
    }
}

public sealed class BranchQueryValidator : AbstractValidator<BranchQuery>
{
    public BranchQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, PageQuery.MaxPageSize);
        RuleFor(q => q.Search).MaximumLength(100);
        RuleFor(q => q.Type!).MustBeEnumName<BranchQuery, BranchType>("Type")
            .When(q => !string.IsNullOrWhiteSpace(q.Type))
            .OverridePropertyName(nameof(BranchQuery.Type));
    }
}

public sealed class UpdateWarehouseRequestValidator : AbstractValidator<UpdateWarehouseRequest>
{
    public UpdateWarehouseRequestValidator()
    {
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

public sealed class WarehouseQueryValidator : AbstractValidator<WarehouseQuery>
{
    public WarehouseQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, PageQuery.MaxPageSize);
        RuleFor(q => q.Search).MaximumLength(100);
    }
}

public sealed class WaitingQueryValidator : AbstractValidator<WaitingQuery>
{
    public WaitingQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, PageQuery.MaxPageSize);
        RuleFor(q => q.Kind!).MustBeEnumName<WaitingQuery, WaitingKind>("Kind")
            .When(q => !string.IsNullOrWhiteSpace(q.Kind))
            .OverridePropertyName(nameof(WaitingQuery.Kind));
    }
}

public sealed class ScanLogQueryValidator : AbstractValidator<ScanLogQuery>
{
    public ScanLogQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, PageQuery.MaxPageSize);
        RuleFor(q => q.TrackingNumber).MaximumLength(ScanRequestValidator.TrackingNumberInputMaxLength);
        RuleFor(q => q.ScanType!).MustBeEnumName<ScanLogQuery, ScanType>("Scan type")
            .When(q => !string.IsNullOrWhiteSpace(q.ScanType))
            .OverridePropertyName(nameof(ScanLogQuery.ScanType));
        RuleFor(q => q.Outcome!).MustBeEnumName<ScanLogQuery, ScanOutcome>("Outcome")
            .When(q => !string.IsNullOrWhiteSpace(q.Outcome))
            .OverridePropertyName(nameof(ScanLogQuery.Outcome));
        RuleFor(q => q.To)
            .GreaterThanOrEqualTo(q => q.From!.Value)
            .When(q => q.From.HasValue && q.To.HasValue)
            .WithMessage("The end date cannot be before the start date.");
    }
}

public sealed class StaffQueryValidator : AbstractValidator<StaffQuery>
{
    public StaffQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, PageQuery.MaxPageSize);
        RuleFor(q => q.Search).MaximumLength(100);
        RuleFor(q => q.Role)
            .Must(r => Roles.Staff.Contains(r!, StringComparer.OrdinalIgnoreCase))
            .When(q => !string.IsNullOrWhiteSpace(q.Role))
            .WithMessage($"Role must be one of: {string.Join(", ", Roles.Staff)}.");
    }
}

public sealed class AssignStaffRequestValidator : AbstractValidator<AssignStaffRequest>
{
    public AssignStaffRequestValidator()
    {
        RuleFor(r => r.BranchId).NotEmpty();
        RuleFor(r => r.WarehouseId).NotEqual(Guid.Empty).When(r => r.WarehouseId.HasValue);
    }
}
