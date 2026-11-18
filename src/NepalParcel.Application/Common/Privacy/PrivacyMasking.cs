namespace NepalParcel.Application.Common.Privacy;

/// <summary>Masks personal data before it is written to logs, audit trails or shown to people who should not see it in full.</summary>
public static class PrivacyMasking
{
    /// <summary>"jyoti@example.com" → "j***@example.com".</summary>
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return "***";
        }

        var at = email.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 ? "***" : $"{email[0]}***{email[at..]}";
    }

    /// <summary>"9841234567" → "98******67".</summary>
    public static string MaskPhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone) || phone.Length < 6)
        {
            return "***";
        }

        return string.Concat(phone.AsSpan(0, 2), new string('*', phone.Length - 4), phone.AsSpan(phone.Length - 2));
    }
}
