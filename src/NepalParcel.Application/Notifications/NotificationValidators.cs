using FluentValidation;
using NepalParcel.Contracts.Notifications;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Notifications;

namespace NepalParcel.Application.Notifications;

internal static class NotificationValidation
{
    public static bool IsDefinedName<TEnum>(string? value)
        where TEnum : struct, Enum =>
        !string.IsNullOrWhiteSpace(value) && !int.TryParse(value, out _) && Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed);

    public static bool IsMessagingChannel(string? value) =>
        IsDefinedName<NotificationChannel>(value)
        && Enum.Parse<NotificationChannel>(value!, ignoreCase: true) is NotificationChannel.Sms or NotificationChannel.WhatsApp;
}

internal sealed class NotificationMessageQueryValidator : AbstractValidator<NotificationMessageQuery>
{
    public NotificationMessageQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 100);
        RuleFor(q => q.Channel).Must(NotificationValidation.IsMessagingChannel).When(q => !string.IsNullOrWhiteSpace(q.Channel)).WithMessage("Channel must be Sms or WhatsApp.");
        RuleFor(q => q.Status).Must(NotificationValidation.IsDefinedName<OutboxStatus>).When(q => !string.IsNullOrWhiteSpace(q.Status))
            .WithMessage($"Status must be one of: {string.Join(", ", Enum.GetNames<OutboxStatus>())}.");
        RuleFor(q => q.DeliveryStatus).Must(NotificationValidation.IsDefinedName<DeliveryStatus>).When(q => !string.IsNullOrWhiteSpace(q.DeliveryStatus))
            .WithMessage($"DeliveryStatus must be one of: {string.Join(", ", Enum.GetNames<DeliveryStatus>())}.");
        RuleFor(q => q.Audience).Must(NotificationValidation.IsDefinedName<NotificationAudience>).When(q => !string.IsNullOrWhiteSpace(q.Audience))
            .WithMessage("Audience must be Sender or Receiver.");
        RuleFor(q => q.TemplateKey).MaximumLength(100);
        RuleFor(q => q.Search).MaximumLength(40);
        RuleFor(q => q.To).GreaterThanOrEqualTo(q => q.From!.Value).When(q => q.From is not null && q.To is not null)
            .WithMessage("The end date cannot be before the start date.");
    }
}

internal sealed class OptOutQueryValidator : AbstractValidator<OptOutQuery>
{
    public OptOutQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 100);
        RuleFor(q => q.Channel).Must(NotificationValidation.IsMessagingChannel).When(q => !string.IsNullOrWhiteSpace(q.Channel)).WithMessage("Channel must be Sms or WhatsApp.");
    }
}

internal sealed class RequeueNotificationRequestValidator : AbstractValidator<RequeueNotificationRequest>
{
    public RequeueNotificationRequestValidator() => RuleFor(r => r.Reason).MaximumLength(300);
}

internal sealed class UpdateNotificationTemplateRequestValidator : AbstractValidator<UpdateNotificationTemplateRequest>
{
    public UpdateNotificationTemplateRequestValidator()
    {
        RuleFor(r => r.Body).NotEmpty().MaximumLength(NotificationTemplate.MaxBodyLength);
        RuleFor(r => r.ProviderTemplateName)
            .MaximumLength(100)
            .Matches("^[a-z0-9_]+$")
            .When(r => !string.IsNullOrWhiteSpace(r.ProviderTemplateName))
            .WithMessage("A WhatsApp template name uses lowercase letters, digits and underscores only.");
    }
}

internal sealed class PreviewTemplateRequestValidator : AbstractValidator<PreviewTemplateRequest>
{
    public PreviewTemplateRequestValidator()
    {
        RuleFor(r => r.Key).NotEmpty().Must(k => NotificationTemplateKeys.All.Contains(k, StringComparer.Ordinal)).WithMessage("Unknown message key.");
        RuleFor(r => r.Body).NotEmpty().MaximumLength(NotificationTemplate.MaxBodyLength);
    }
}

internal sealed class CreateOptOutRequestValidator : AbstractValidator<CreateOptOutRequest>
{
    public CreateOptOutRequestValidator()
    {
        RuleFor(r => r.PhoneNumber).NotEmpty().Must(NepalPhoneNumber.IsValid).WithMessage("Enter a valid Nepali phone number.");
        RuleFor(r => r.Channel).Must(NotificationValidation.IsMessagingChannel).WithMessage("Channel must be Sms or WhatsApp.");
    }
}

internal sealed class UpdateNotificationPreferencesRequestValidator : AbstractValidator<UpdateNotificationPreferencesRequest>
{
    public UpdateNotificationPreferencesRequestValidator() =>
        RuleFor(r => r).Must(r => r.Sms is not null || r.WhatsApp is not null).WithMessage("Give at least one choice to change.").OverridePropertyName("preferences");
}

internal sealed class SimulateStatusRequestValidator : AbstractValidator<SimulateStatusRequest>
{
    public SimulateStatusRequestValidator()
    {
        RuleFor(r => r.Status).Must(s => NotificationWebhookService.ParseStatus(s) is not null).WithMessage("Status must be sent, delivered, read or failed.");
        RuleFor(r => r.Error).MaximumLength(450);
    }
}
