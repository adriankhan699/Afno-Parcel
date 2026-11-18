using System.Text.RegularExpressions;

namespace NepalParcel.Domain.Common;

/// <summary>
/// Validation and normalization for Nepali phone numbers.
/// Mobile: 10 digits starting with 96/97/98 (optionally prefixed by +977 / 977).
/// Landline: area code starting with 0 followed by 6–8 digits (e.g. 01-4XXXXXX).
/// </summary>
public static partial class NepalPhoneNumber
{
    public const int MaxLength = 20;

    public static bool IsValid(string? value) => TryNormalize(value, out _);

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var compact = SeparatorRegex().Replace(value.Trim(), string.Empty);
        if (compact.StartsWith("+977", StringComparison.Ordinal))
        {
            compact = compact[4..];
        }
        else if (compact.StartsWith("977", StringComparison.Ordinal) && compact.Length == 13)
        {
            compact = compact[3..];
        }

        if (MobileRegex().IsMatch(compact) || LandlineRegex().IsMatch(compact))
        {
            normalized = compact;
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"[\s\-()]")]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex(@"^9[678]\d{8}$")]
    private static partial Regex MobileRegex();

    [GeneratedRegex(@"^0\d{7,9}$")]
    private static partial Regex LandlineRegex();
}
