using FluentValidation;
using NepalParcel.Contracts.Common;
using NepalParcel.Domain.Common;

namespace NepalParcel.Application.Common.Validation;

/// <summary>Validates a Nepal address DTO against the same rules the domain enforces.</summary>
public sealed class AddressDtoValidator : AbstractValidator<AddressDto>
{
    public AddressDtoValidator()
    {
        RuleFor(a => a.Province)
            .NotEmpty()
            .Must(p => Enum.TryParse<Province>(p, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && !int.TryParse(p, out _))
            .WithMessage($"Province must be one of: {string.Join(", ", Enum.GetNames<Province>())}.");

        RuleFor(a => a.District).NotEmpty().MaximumLength(NepalAddress.DistrictMaxLength);
        RuleFor(a => a.District)
            .Must((a, district) => !Enum.TryParse<Province>(a.Province, ignoreCase: true, out var province)
                                   || int.TryParse(a.Province, out _)
                                   || NepalDistricts.Contains(province, district))
            .When(a => !string.IsNullOrWhiteSpace(a.District))
            .WithMessage(a => $"'{a.District}' is not a district of {a.Province} Province.");
        RuleFor(a => a.Municipality).NotEmpty().MaximumLength(NepalAddress.MunicipalityMaxLength);
        RuleFor(a => a.WardNumber).InclusiveBetween(1, NepalAddress.MaxWardNumber);
        RuleFor(a => a.StreetOrTole).MaximumLength(NepalAddress.StreetMaxLength);
        RuleFor(a => a.Landmark).MaximumLength(NepalAddress.LandmarkMaxLength);
        RuleFor(a => a.PostalCode)
            .Matches(@"^\d{5}$")
            .When(a => !string.IsNullOrWhiteSpace(a.PostalCode))
            .WithMessage("Postal code must be exactly 5 digits.");
    }
}
