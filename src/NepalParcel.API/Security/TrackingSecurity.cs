using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NepalParcel.API.Options;
using NepalParcel.Application.Tracking;

namespace NepalParcel.API.Security;

/// <summary>
/// Keyed hashing for one-time codes and signing for the access tokens that follow a correct code. Both keys are derived from
/// the server's signing secret with different labels, so a value made for one purpose can never be used for the other.
/// </summary>
internal sealed class TrackingSecurity : ITrackingSecurity
{
    private readonly byte[] _otpKey;
    private readonly byte[] _tokenKey;

    public TrackingSecurity(IOptions<JwtOptions> jwt)
    {
        ArgumentNullException.ThrowIfNull(jwt);
        var master = Encoding.UTF8.GetBytes(jwt.Value.SigningKey);
        _otpKey = HMACSHA256.HashData(master, "afno-parcel/tracking-otp"u8);
        _tokenKey = HMACSHA256.HashData(master, "afno-parcel/tracking-access"u8);
    }

    public string HashOtp(Guid parcelId, string code) =>
        Convert.ToHexString(HMACSHA256.HashData(_otpKey, Encoding.UTF8.GetBytes($"{parcelId:N}:{code}")));

    public bool VerifyOtp(Guid parcelId, string code, string expectedHash) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(HashOtp(parcelId, code)),
            Encoding.ASCII.GetBytes(expectedHash));

    public string CreateAccessToken(string trackingNumber, DateTimeOffset expiresAtUtc)
    {
        var payload = Encoding.UTF8.GetBytes($"{trackingNumber}|{expiresAtUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}");
        return $"{Base64Url.EncodeToString(payload)}.{Base64Url.EncodeToString(HMACSHA256.HashData(_tokenKey, payload))}";
    }

    public bool ValidateAccessToken(string token, string trackingNumber, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var parts = token.Split('.');
        if (parts.Length != 2)
        {
            return false;
        }

        byte[] payload;
        byte[] signature;
        try
        {
            payload = Base64Url.DecodeFromChars(parts[0]);
            signature = Base64Url.DecodeFromChars(parts[1]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(_tokenKey, payload), signature))
        {
            return false;
        }

        var fields = Encoding.UTF8.GetString(payload).Split('|');
        return fields.Length == 2
               && string.Equals(fields[0], trackingNumber, StringComparison.Ordinal)
               && long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expires)
               && expires > nowUtc.ToUnixTimeSeconds();
    }
}
