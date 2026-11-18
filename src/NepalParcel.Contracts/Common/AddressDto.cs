namespace NepalParcel.Contracts.Common;

/// <summary>A Nepal postal address.</summary>
/// <param name="Province">Province name: Koshi, Madhesh, Bagmati, Gandaki, Lumbini, Karnali or Sudurpashchim.</param>
/// <param name="District">District, e.g. Kathmandu.</param>
/// <param name="Municipality">Metropolitan / sub-metropolitan / municipality / rural municipality.</param>
/// <param name="WardNumber">Ward number (1–35).</param>
/// <param name="StreetOrTole">Street or tole (optional).</param>
/// <param name="Landmark">Nearby landmark to help delivery staff (optional).</param>
/// <param name="PostalCode">5-digit postal code (optional).</param>
public sealed record AddressDto(
    string Province,
    string District,
    string Municipality,
    int WardNumber,
    string? StreetOrTole = null,
    string? Landmark = null,
    string? PostalCode = null);
