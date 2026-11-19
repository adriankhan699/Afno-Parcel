using FluentValidation;
using Microsoft.Extensions.Options;
using NepalParcel.Application.Common.Validation;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Parcels;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Payments;

namespace NepalParcel.Application.Parcels;

internal static class ParcelRuleExtensions
{
    /// <summary>The text must name a defined member of <typeparamref name="TEnum"/> (not a number).</summary>
    public static IRuleBuilderOptions<T, string> MustBeEnumName<T, TEnum>(this IRuleBuilder<T, string> rule, string label)
        where TEnum : struct, Enum =>
        rule.NotEmpty()
            .Must(IsEnumName<TEnum>)
            .WithMessage($"{label} must be one of: {string.Join(", ", Enum.GetNames<TEnum>())}.");

    public static bool IsEnumName<TEnum>(string? value)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && !int.TryParse(value, out _);

    public static IRuleBuilderOptions<T, decimal> MustBeValidWeight<T>(this IRuleBuilder<T, decimal> rule, BookingOptions options) =>
        rule.GreaterThan(0)
            .LessThanOrEqualTo(options.MaxWeightKg)
            .WithMessage($"Weight must be at most {options.MaxWeightKg:0.###} kg.")
            .PrecisionScale(10, 3, false)
            .WithMessage("Weight can have at most 3 decimal places.");

    public static IRuleBuilderOptions<T, decimal?> MustBeValidDimension<T>(this IRuleBuilder<T, decimal?> rule, BookingOptions options) =>
        rule.GreaterThan(0)
            .LessThanOrEqualTo(options.MaxDimensionCm)
            .WithMessage($"Each side must be at most {options.MaxDimensionCm:0.###} cm.")
            .PrecisionScale(8, 2, false)
            .WithMessage("Dimensions can have at most 2 decimal places.");

    public static IRuleBuilderOptions<T, decimal?> MustBeValidMoney<T>(this IRuleBuilder<T, decimal?> rule, decimal max, string label) =>
        rule.GreaterThanOrEqualTo(0)
            .LessThanOrEqualTo(max)
            .WithMessage($"{label} must be at most NPR {max:N0}.")
            .PrecisionScale(18, 2, false)
            .WithMessage($"{label} can have at most 2 decimal places.");

    /// <summary>Length, width and height are given together or not at all.</summary>
    public static void RequireAllOrNoDimensions<T>(
        this AbstractValidator<T> validator,
        Func<T, decimal?> length,
        Func<T, decimal?> width,
        Func<T, decimal?> height)
    {
        validator.RuleFor(r => r)
            .Must(r => (length(r) is null) == (width(r) is null) && (width(r) is null) == (height(r) is null))
            .OverridePropertyName("Dimensions")
            .WithMessage("Give length, width and height together, or leave all three empty.");
    }
}

/// <summary>A sender or receiver on a booking.</summary>
public sealed class BookingPartyDtoValidator : AbstractValidator<BookingPartyDto>
{
    public BookingPartyDtoValidator()
    {
        RuleFor(p => p.FullName).NotEmpty().MaximumLength(Parcel.NameMaxLength);

        RuleFor(p => p.PhoneNumber)
            .NotEmpty()
            .MaximumLength(NepalPhoneNumber.MaxLength)
            .Must(NepalPhoneNumber.IsValid)
            .WithMessage("Phone number must be a valid Nepali mobile (98XXXXXXXX) or landline (01-XXXXXXX) number.");

        RuleFor(p => p.Address).NotNull().SetValidator(new AddressDtoValidator());
        RuleFor(p => p.AddressInstructions).MaximumLength(Parcel.InstructionsMaxLength);
    }
}

public sealed class BookParcelRequestValidator : AbstractValidator<BookParcelRequest>
{
    public BookParcelRequestValidator(IOptions<BookingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.Value;

        RuleFor(r => r.Sender).NotNull().SetValidator(new BookingPartyDtoValidator());
        RuleFor(r => r.Receiver).NotNull().SetValidator(new BookingPartyDtoValidator());
        RuleFor(r => r.OriginBranchId).NotEmpty();

        RuleFor(r => r.Description).NotEmpty().MaximumLength(Parcel.DescriptionMaxLength);
        RuleFor(r => r.Quantity).InclusiveBetween(1, Parcel.MaxQuantity);
        RuleFor(r => r.WeightKg).MustBeValidWeight(settings);

        RuleFor(r => r.LengthCm).MustBeValidDimension(settings);
        RuleFor(r => r.WidthCm).MustBeValidDimension(settings);
        RuleFor(r => r.HeightCm).MustBeValidDimension(settings);
        this.RequireAllOrNoDimensions(r => r.LengthCm, r => r.WidthCm, r => r.HeightCm);

        RuleFor(r => r.DeclaredValue).MustBeValidMoney(settings.MaxDeclaredValue, "Declared value");
        RuleFor(r => r.CodAmount).MustBeValidMoney(settings.MaxCodAmount, "COD amount");

        RuleFor(r => r.ServiceType).MustBeEnumName<BookParcelRequest, ServiceType>("Service type");
        RuleFor(r => r.PaymentMethod).MustBeEnumName<BookParcelRequest, PaymentMethod>("Payment method");

        RuleFor(r => r.PaymentReference).MaximumLength(100);
        RuleFor(r => r.Notes).MaximumLength(Parcel.NotesMaxLength);
    }
}

public sealed class QuoteRequestValidator : AbstractValidator<QuoteRequest>
{
    public QuoteRequestValidator(IOptions<BookingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.Value;

        RuleFor(r => r.OriginBranchId).NotEmpty();
        RuleFor(r => r.Destination).NotNull().SetValidator(new AddressDtoValidator());
        RuleFor(r => r.ServiceType).MustBeEnumName<QuoteRequest, ServiceType>("Service type");
        RuleFor(r => r.WeightKg).MustBeValidWeight(settings);

        RuleFor(r => r.LengthCm).MustBeValidDimension(settings);
        RuleFor(r => r.WidthCm).MustBeValidDimension(settings);
        RuleFor(r => r.HeightCm).MustBeValidDimension(settings);
        this.RequireAllOrNoDimensions(r => r.LengthCm, r => r.WidthCm, r => r.HeightCm);

        RuleFor(r => r.CodAmount).MustBeValidMoney(settings.MaxCodAmount, "COD amount");
    }
}

public sealed class ParcelSearchQueryValidator : AbstractValidator<ParcelSearchQuery>
{
    public const int SearchMaxLength = 100;

    public ParcelSearchQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, PageQuery.MaxPageSize);
        RuleFor(q => q.Search).MaximumLength(SearchMaxLength);

        RuleFor(q => q.Status!).MustBeEnumName<ParcelSearchQuery, ParcelStatusCode>("Status")
            .When(q => !string.IsNullOrWhiteSpace(q.Status))
            .OverridePropertyName(nameof(ParcelSearchQuery.Status));
        RuleFor(q => q.ServiceType!).MustBeEnumName<ParcelSearchQuery, ServiceType>("Service type")
            .When(q => !string.IsNullOrWhiteSpace(q.ServiceType))
            .OverridePropertyName(nameof(ParcelSearchQuery.ServiceType));
        RuleFor(q => q.PaymentMethod!).MustBeEnumName<ParcelSearchQuery, PaymentMethod>("Payment method")
            .When(q => !string.IsNullOrWhiteSpace(q.PaymentMethod))
            .OverridePropertyName(nameof(ParcelSearchQuery.PaymentMethod));
        RuleFor(q => q.Channel!).MustBeEnumName<ParcelSearchQuery, BookingChannel>("Channel")
            .When(q => !string.IsNullOrWhiteSpace(q.Channel))
            .OverridePropertyName(nameof(ParcelSearchQuery.Channel));

        RuleFor(q => q.BookedTo)
            .GreaterThanOrEqualTo(q => q.BookedFrom!.Value)
            .When(q => q.BookedFrom.HasValue && q.BookedTo.HasValue)
            .WithMessage("The end date cannot be before the start date.");
    }
}

public sealed class CancelParcelRequestValidator : AbstractValidator<CancelParcelRequest>
{
    public CancelParcelRequestValidator()
    {
        RuleFor(r => r.Reason)
            .NotEmpty()
            .MinimumLength(CancellationPolicy.ReasonMinLength)
            .MaximumLength(CancellationPolicy.ReasonMaxLength);
    }
}

public sealed class ConfirmPaymentRequestValidator : AbstractValidator<ConfirmPaymentRequest>
{
    public ConfirmPaymentRequestValidator()
    {
        RuleFor(r => r.Reference).MaximumLength(100);
    }
}

public sealed class CreatePricingRuleRequestValidator : AbstractValidator<CreatePricingRuleRequest>
{
    public const decimal MaxPrice = 10_000_000m;

    public CreatePricingRuleRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(150);
        RuleFor(r => r.ServiceType).MustBeEnumName<CreatePricingRuleRequest, ServiceType>("Service type");
        RuleFor(r => r.MinWeightKg).GreaterThanOrEqualTo(0).PrecisionScale(10, 3, false);
        RuleFor(r => r.MaxWeightKg).GreaterThan(r => r.MinWeightKg).PrecisionScale(10, 3, false);
        RuleFor(r => r.BasePrice).InclusiveBetween(0, MaxPrice).PrecisionScale(18, 2, false);
        RuleFor(r => r.PricePerKg).InclusiveBetween(0, MaxPrice).PrecisionScale(18, 2, false);
        RuleFor(r => r.CodFeePercent).InclusiveBetween(0, 100).PrecisionScale(5, 2, false);
        RuleFor(r => r.Priority).InclusiveBetween(0, 1000);

        RuleFor(r => r.EffectiveToUtc)
            .GreaterThan(r => r.EffectiveFromUtc)
            .When(r => r.EffectiveFromUtc.HasValue && r.EffectiveToUtc.HasValue)
            .WithMessage("The end must be after the start.");
    }
}
