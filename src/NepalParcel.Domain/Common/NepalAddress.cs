namespace NepalParcel.Domain.Common;

/// <summary>
/// A postal address in Nepal: Province → District → Municipality / Rural Municipality → Ward,
/// plus optional tole/street, landmark and 5-digit postal code.
/// </summary>
public sealed record NepalAddress
{
    public const int MaxWardNumber = 35;
    public const int DistrictMaxLength = 60;
    public const int MunicipalityMaxLength = 100;
    public const int StreetMaxLength = 200;
    public const int LandmarkMaxLength = 200;
    public const int PostalCodeLength = 5;

    // Required by EF Core for owned-type materialization.
    private NepalAddress()
    {
        District = string.Empty;
        Municipality = string.Empty;
    }

    private NepalAddress(
        Province province,
        string district,
        string municipality,
        int wardNumber,
        string? streetOrTole,
        string? landmark,
        string? postalCode)
    {
        Province = province;
        District = district;
        Municipality = municipality;
        WardNumber = wardNumber;
        StreetOrTole = streetOrTole;
        Landmark = landmark;
        PostalCode = postalCode;
    }

    public Province Province { get; private init; }

    public string District { get; private init; }

    public string Municipality { get; private init; }

    public int WardNumber { get; private init; }

    public string? StreetOrTole { get; private init; }

    public string? Landmark { get; private init; }

    public string? PostalCode { get; private init; }

    public static NepalAddress Create(
        Province province,
        string district,
        string municipality,
        int wardNumber,
        string? streetOrTole = null,
        string? landmark = null,
        string? postalCode = null)
    {
        if (!Enum.IsDefined(province))
        {
            throw new DomainException($"'{province}' is not a valid province of Nepal.");
        }

        if (wardNumber is < 1 or > MaxWardNumber)
        {
            throw new DomainException($"Ward number must be between 1 and {MaxWardNumber}.");
        }

        var normalizedPostal = Guard.Optional(postalCode, "Postal code", PostalCodeLength);
        if (normalizedPostal is not null &&
            (normalizedPostal.Length != PostalCodeLength || !normalizedPostal.All(char.IsAsciiDigit)))
        {
            throw new DomainException("Postal code must be exactly 5 digits.");
        }

        return new NepalAddress(
            province,
            Guard.NotBlank(district, "District", DistrictMaxLength),
            Guard.NotBlank(municipality, "Municipality", MunicipalityMaxLength),
            wardNumber,
            Guard.Optional(streetOrTole, "Street / tole", StreetMaxLength),
            Guard.Optional(landmark, "Landmark", LandmarkMaxLength),
            normalizedPostal);
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (StreetOrTole is not null)
        {
            parts.Add(StreetOrTole);
        }

        parts.Add($"{Municipality}-{WardNumber}");
        parts.Add(District);
        parts.Add($"{Province} Province");
        if (PostalCode is not null)
        {
            parts.Add(PostalCode);
        }

        return string.Join(", ", parts);
    }
}
