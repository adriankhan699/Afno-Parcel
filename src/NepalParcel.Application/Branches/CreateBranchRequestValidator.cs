using FluentValidation;
using NepalParcel.Application.Common.Validation;
using NepalParcel.Contracts.Branches;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Common;

namespace NepalParcel.Application.Branches;

public sealed class CreateBranchRequestValidator : AbstractValidator<CreateBranchRequest>
{
    public CreateBranchRequestValidator()
    {
        RuleFor(r => r.Code)
            .NotEmpty()
            .MaximumLength(Branch.CodeMaxLength)
            .Matches("^[A-Za-z0-9]+$").WithMessage("Code may contain only letters and digits.");

        RuleFor(r => r.Name).NotEmpty().MaximumLength(Branch.NameMaxLength);

        RuleFor(r => r.Type)
            .NotEmpty()
            .Must(t => Enum.TryParse<BranchType>(t, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && !int.TryParse(t, out _))
            .WithMessage($"Type must be one of: {string.Join(", ", Enum.GetNames<BranchType>())}.");

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
