using FluentValidation;
using NepalParcel.Contracts.Auth;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Identity;

namespace NepalParcel.Application.Auth;

internal static class AuthRules
{
    public const int PasswordMinLength = 10;
    public const int PasswordMaxLength = 128;
    public const int EmailMaxLength = 256;

    public static IRuleBuilderOptions<T, string> ValidEmail<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MaximumLength(EmailMaxLength).EmailAddress();

    /// <summary>Basic shape only; complexity rules are enforced (and reported) by ASP.NET Core Identity.</summary>
    public static IRuleBuilderOptions<T, string> ValidNewPassword<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MinimumLength(PasswordMinLength).MaximumLength(PasswordMaxLength);
}

public sealed class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(r => r.FullName).NotEmpty().MaximumLength(150);
        RuleFor(r => r.Email).ValidEmail();
        RuleFor(r => r.PhoneNumber)
            .NotEmpty()
            .Must(NepalPhoneNumber.IsValid)
            .WithMessage("Phone number must be a valid Nepali mobile (98XXXXXXXX) or landline (01-XXXXXXX) number.");
        RuleFor(r => r.Password).ValidNewPassword();
    }
}

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(r => r.Email).NotEmpty().MaximumLength(AuthRules.EmailMaxLength);

        // Upper bound prevents very long inputs from being used to burn CPU in the password hasher.
        RuleFor(r => r.Password).NotEmpty().MaximumLength(AuthRules.PasswordMaxLength);
    }
}

public sealed class RefreshTokenRequestValidator : AbstractValidator<RefreshTokenRequest>
{
    public RefreshTokenRequestValidator()
    {
        RuleFor(r => r.RefreshToken).NotEmpty().MaximumLength(256);
    }
}

public sealed class LogoutRequestValidator : AbstractValidator<LogoutRequest>
{
    public LogoutRequestValidator()
    {
        RuleFor(r => r.RefreshToken).NotEmpty().MaximumLength(256);
    }
}

public sealed class CreateStaffUserRequestValidator : AbstractValidator<CreateStaffUserRequest>
{
    public CreateStaffUserRequestValidator()
    {
        RuleFor(r => r.FullName).NotEmpty().MaximumLength(150);
        RuleFor(r => r.Email).ValidEmail();
        RuleFor(r => r.PhoneNumber)
            .Must(p => string.IsNullOrWhiteSpace(p) || NepalPhoneNumber.IsValid(p))
            .WithMessage("Phone number must be a valid Nepali phone number.");
        RuleFor(r => r.Password).ValidNewPassword();
        RuleFor(r => r.Role)
            .NotEmpty()
            .Must(role => Roles.AssignableStaff.Contains(role, StringComparer.Ordinal))
            .WithMessage($"Role must be one of: {string.Join(", ", Roles.AssignableStaff)}.");
    }
}
