using System.Security.Cryptography;

namespace NepalParcel.Domain.Payments;

/// <summary>
/// Our own reference for a payment attempt, such as <c>AFP7K2M9Q4XB3T8WRC5H</c>: a prefix and 17 random characters from an
/// unambiguous alphabet. Random, so nobody can guess another customer's reference; unique by a database index.
/// </summary>
public static class MerchantReferences
{
    public const string Prefix = "AFP";
    public const int RandomLength = 17;

    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static string New()
    {
        Span<char> chars = stackalloc char[RandomLength];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return Prefix + new string(chars);
    }

    /// <summary>Whether the text has the shape of one of our references (it does not prove the reference exists).</summary>
    public static bool LooksValid(string? value) =>
        value is not null
        && value.Length == Prefix.Length + RandomLength
        && value.StartsWith(Prefix, StringComparison.Ordinal)
        && value.AsSpan(Prefix.Length).IndexOfAnyExcept(Alphabet) < 0;
}
