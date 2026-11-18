namespace NepalParcel.Domain.Common;

/// <summary>Small helpers for enforcing invariants with <see cref="DomainException"/>.</summary>
internal static class Guard
{
    public static string NotBlank(string? value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException($"{name} is required.");
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new DomainException($"{name} must be at most {maxLength} characters.");
        }

        return trimmed;
    }

    public static string? Optional(string? value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new DomainException($"{name} must be at most {maxLength} characters.");
        }

        return trimmed;
    }

    public static decimal NonNegative(decimal value, string name)
    {
        if (value < 0)
        {
            throw new DomainException($"{name} cannot be negative.");
        }

        return decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    }

    public static decimal Positive(decimal value, string name, int decimals = 3)
    {
        if (value <= 0)
        {
            throw new DomainException($"{name} must be greater than zero.");
        }

        return decimal.Round(value, decimals, MidpointRounding.AwayFromZero);
    }

    public static string NormalizePhone(string? value, string name = "Phone number")
    {
        if (!NepalPhoneNumber.TryNormalize(value, out var phone))
        {
            throw new DomainException($"{name} is not a valid Nepali phone number.");
        }

        return phone;
    }

    public static Guid NotEmpty(Guid value, string name)
    {
        if (value == Guid.Empty)
        {
            throw new DomainException($"{name} is required.");
        }

        return value;
    }
}
