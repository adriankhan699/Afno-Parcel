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
