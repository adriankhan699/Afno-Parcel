using FluentValidation;
using NepalParcel.Application.Parcels;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Payments;
using NepalParcel.Domain.Payments;

namespace NepalParcel.Application.Payments;

internal static class PaymentRules
{
    public const int MaxReportDays = 366;
    public const int RemarksMaxLength = 500;
    public const int MaxCallbackEntries = 20;

    public static readonly string[] OnlineProviders = [nameof(PaymentMethod.Esewa), nameof(PaymentMethod.Khalti)];

    public static readonly string[] TestOutcomes = ["Success", "Failure", "Cancel"];

    public static bool IsOnlineProvider(string? value) =>
        OnlineProviders.Contains(value, StringComparer.OrdinalIgnoreCase);
}

public sealed class InitiatePaymentRequestValidator : AbstractValidator<InitiatePaymentRequest>
{
    public InitiatePaymentRequestValidator()
    {
        RuleFor(r => r.ParcelId).NotEmpty();
        RuleFor(r => r.Provider)
            .NotEmpty()
            .Must(PaymentRules.IsOnlineProvider)
            .WithMessage($"Provider must be one of: {string.Join(", ", PaymentRules.OnlineProviders)}.");
    }
}

public sealed class PaymentCallbackRequestValidator : AbstractValidator<PaymentCallbackRequest>
{
    public PaymentCallbackRequestValidator()
    {
        RuleFor(r => r.Reference).NotEmpty().Must(MerchantReferences.LooksValid).WithMessage("That is not a valid payment reference.");
        RuleFor(r => r.ProviderData)
            .Must(d => d is null || d.Count <= PaymentRules.MaxCallbackEntries)
            .WithMessage($"At most {PaymentRules.MaxCallbackEntries} provider values are accepted.")
            .Must(d => d is null || d.All(kv => kv.Key.Length is > 0 and <= 50 && (kv.Value?.Length ?? 0) <= 500))
            .WithMessage("A provider value is too long.");
    }
}

public sealed class TestGatewayCompleteRequestValidator : AbstractValidator<TestGatewayCompleteRequest>
{
    public TestGatewayCompleteRequestValidator()
    {
        RuleFor(r => r.Outcome)
            .NotEmpty()
            .Must(o => PaymentRules.TestOutcomes.Contains(o, StringComparer.OrdinalIgnoreCase))
            .WithMessage($"Outcome must be one of: {string.Join(", ", PaymentRules.TestOutcomes)}.");
    }
}

public sealed class RecordOfficeCashRequestValidator : AbstractValidator<RecordOfficeCashRequest>
{
    public RecordOfficeCashRequestValidator()
    {
        RuleFor(r => r.ParcelId).NotEmpty();
        RuleFor(r => r.AmountReceived).GreaterThan(0).PrecisionScale(18, 2, false).WithMessage("The amount can have at most 2 decimal places.");
        RuleFor(r => r.Note).MaximumLength(100);
    }
}

public sealed class PaymentTransactionQueryValidator : AbstractValidator<PaymentTransactionQuery>
{
    public PaymentTransactionQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, PageQuery.MaxPageSize);
        RuleFor(q => q.Search).MaximumLength(100);
        RuleFor(q => q.Provider!)
            .Must(p => ParcelRuleExtensions.IsEnumName<PaymentMethod>(p))
            .When(q => !string.IsNullOrWhiteSpace(q.Provider))
            .WithMessage($"Provider must be one of: {string.Join(", ", Enum.GetNames<PaymentMethod>())}.")
            .OverridePropertyName(nameof(PaymentTransactionQuery.Provider));
        RuleFor(q => q.Status!)
            .Must(s => ParcelRuleExtensions.IsEnumName<TransactionStatus>(s))
            .When(q => !string.IsNullOrWhiteSpace(q.Status))
            .WithMessage($"Status must be one of: {string.Join(", ", Enum.GetNames<TransactionStatus>())}.")
            .OverridePropertyName(nameof(PaymentTransactionQuery.Status));
        RuleFor(q => q.To)
            .GreaterThanOrEqualTo(q => q.From!.Value)
            .When(q => q.From.HasValue && q.To.HasValue)
            .WithMessage("The end date cannot be before the start date.");
    }
}

public sealed class CreateSettlementRequestValidator : AbstractValidator<CreateSettlementRequest>
{
    public CreateSettlementRequestValidator()
    {
        RuleFor(r => r.Remarks).MaximumLength(300);
    }
}

public sealed class VerifySettlementRequestValidator : AbstractValidator<VerifySettlementRequest>
{
    public VerifySettlementRequestValidator()
    {
        RuleFor(r => r.CountedAmount).GreaterThanOrEqualTo(0).PrecisionScale(18, 2, false).WithMessage("The amount can have at most 2 decimal places.");
        RuleFor(r => r.Remarks).MaximumLength(PaymentRules.RemarksMaxLength);
    }
}

public sealed class ResolveSettlementRequestValidator : AbstractValidator<ResolveSettlementRequest>
{
    public ResolveSettlementRequestValidator()
    {
        RuleFor(r => r.Remarks).NotEmpty().MinimumLength(5).MaximumLength(PaymentRules.RemarksMaxLength);
    }
}

public sealed class SettlementQueryValidator : AbstractValidator<SettlementQuery>
{
    public SettlementQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, PageQuery.MaxPageSize);
        RuleFor(q => q.Status!)
            .Must(s => ParcelRuleExtensions.IsEnumName<SettlementStatus>(s) && !string.Equals(s, nameof(SettlementStatus.Draft), StringComparison.OrdinalIgnoreCase))
            .When(q => !string.IsNullOrWhiteSpace(q.Status))
            .WithMessage("Status must be Submitted, Verified or Disputed.")
            .OverridePropertyName(nameof(SettlementQuery.Status));
        RuleFor(q => q.To)
            .GreaterThanOrEqualTo(q => q.From!.Value)
            .When(q => q.From.HasValue && q.To.HasValue)
            .WithMessage("The end date cannot be before the start date.");
    }
}

public sealed class PaymentReportQueryValidator : AbstractValidator<PaymentReportQuery>
{
    public PaymentReportQueryValidator()
    {
        RuleFor(q => q.To)
            .GreaterThanOrEqualTo(q => q.From!.Value)
            .When(q => q.From.HasValue && q.To.HasValue)
            .WithMessage("The end date cannot be before the start date.");
        RuleFor(q => q)
            .Must(q => q.From is null || q.To is null || q.To.Value.DayNumber - q.From.Value.DayNumber < PaymentRules.MaxReportDays)
            .WithMessage($"A report can cover at most {PaymentRules.MaxReportDays} days.")
            .OverridePropertyName(nameof(PaymentReportQuery.To));
    }
}
