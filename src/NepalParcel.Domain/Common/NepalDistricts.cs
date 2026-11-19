namespace NepalParcel.Domain.Common;

/// <summary>
/// The 77 districts of Nepal grouped by province. Used to validate addresses and to seed the service areas.
/// Names use the common English spellings from the Government of Nepal's administrative list.
/// </summary>
public static class NepalDistricts
{
    private static readonly Dictionary<Province, string[]> ByProvince = new()
    {
        [Province.Koshi] =
        [
            "Bhojpur", "Dhankuta", "Ilam", "Jhapa", "Khotang", "Morang", "Okhaldhunga", "Panchthar",
            "Sankhuwasabha", "Solukhumbu", "Sunsari", "Taplejung", "Terhathum", "Udayapur",
        ],
        [Province.Madhesh] =
        [
            "Bara", "Dhanusha", "Mahottari", "Parsa", "Rautahat", "Saptari", "Sarlahi", "Siraha",
        ],
        [Province.Bagmati] =
        [
            "Bhaktapur", "Chitwan", "Dhading", "Dolakha", "Kathmandu", "Kavrepalanchok", "Lalitpur", "Makwanpur",
            "Nuwakot", "Ramechhap", "Rasuwa", "Sindhuli", "Sindhupalchok",
        ],
        [Province.Gandaki] =
        [
            "Baglung", "Gorkha", "Kaski", "Lamjung", "Manang", "Mustang", "Myagdi", "Nawalpur", "Parbat",
            "Syangja", "Tanahun",
        ],
        [Province.Lumbini] =
        [
            "Arghakhanchi", "Banke", "Bardiya", "Dang", "Eastern Rukum", "Gulmi", "Kapilvastu", "Palpa",
            "Parasi", "Pyuthan", "Rolpa", "Rupandehi",
        ],
        [Province.Karnali] =
        [
            "Dailekh", "Dolpa", "Humla", "Jajarkot", "Jumla", "Kalikot", "Mugu", "Salyan", "Surkhet", "Western Rukum",
        ],
        [Province.Sudurpashchim] =
        [
            "Achham", "Baitadi", "Bajhang", "Bajura", "Dadeldhura", "Darchula", "Doti", "Kailali", "Kanchanpur",
        ],
    };

    /// <summary>Every (province, district) pair, in province order.</summary>
    public static IEnumerable<(Province Province, string District)> All =>
        ByProvince.OrderBy(kv => (int)kv.Key).SelectMany(kv => kv.Value.Select(d => (kv.Key, d)));

    public static IReadOnlyList<string> For(Province province) =>
        ByProvince.TryGetValue(province, out var districts) ? districts : [];

    /// <summary>True when <paramref name="district"/> belongs to <paramref name="province"/> (case-insensitive).</summary>
    public static bool Contains(Province province, string? district) =>
        !string.IsNullOrWhiteSpace(district) &&
        For(province).Contains(district.Trim(), StringComparer.OrdinalIgnoreCase);
}
