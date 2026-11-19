using System.Security.Cryptography;

namespace NepalParcel.Domain.Parcels;

/// <summary>
/// Generates and validates tracking numbers such as <c>AFNK7M2Q9XT4B3</c>: the prefix <c>AFN</c>, ten random
/// characters from an unambiguous alphabet (no 0/O/1/I/L) and one check character that catches typos.
/// Uniqueness is guaranteed by a unique database index; callers retry on the (very unlikely) collision.
/// </summary>
public static class TrackingNumbers
{
    public const string Prefix = "AFN";
    public const int RandomLength = 10;
    public const int Length = 14;

    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static string Generate()
    {
        Span<char> chars = stackalloc char[RandomLength];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        var body = new string(chars);
        return Prefix + body + CheckChar(body);
    }

    /// <summary>Upper-cases and strips spaces and dashes, as customers often type them.</summary>
    public static string Normalize(string? input) =>
        string.IsNullOrWhiteSpace(input)
            ? string.Empty
            : input.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    /// <summary>
    /// Finds a valid tracking number in text a scanner or a person supplied: the number itself (in any case, with spaces or
    /// dashes), or a tracking link such as <c>https://example.com/track/AFNK7M2Q9XT4B3</c> as read from a QR code.
    /// </summary>
    public static bool TryExtract(string? text, out string trackingNumber)
    {
        trackingNumber = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var direct = Normalize(text);
        if (IsValid(direct))
        {
            trackingNumber = direct;
            return true;
        }

        // A link or other text: look for something shaped like a tracking number and keep the first one whose check character is right.
        var upper = text.ToUpperInvariant();
        for (var i = 0; i + Length <= upper.Length; i++)
        {
            if (string.CompareOrdinal(upper, i, Prefix, 0, Prefix.Length) != 0)
            {
                continue;
            }

            var candidate = upper.Substring(i, Length);
            if (IsValid(candidate))
            {
                trackingNumber = candidate;
                return true;
            }
        }

        return false;
    }

    public static bool IsValid(string? input)
    {
        var value = Normalize(input);
        if (value.Length != Length || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var body = value.Substring(Prefix.Length, RandomLength);
        return body.All(c => Alphabet.Contains(c, StringComparison.Ordinal)) && value[^1] == CheckChar(body);
    }

    private static char CheckChar(string body)
    {
        var sum = 0;
        for (var i = 0; i < body.Length; i++)
        {
            sum += (i + 1) * Alphabet.IndexOf(body[i], StringComparison.Ordinal);
        }

        return Alphabet[sum % Alphabet.Length];
    }
}
