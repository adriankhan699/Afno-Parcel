using NepalParcel.Contracts.Common;
using NepalParcel.Domain.Common;

namespace NepalParcel.Application.Common.Mapping;

internal static class AddressMappings
{
    public static NepalAddress ToDomain(this AddressDto dto) =>
        NepalAddress.Create(
            Enum.Parse<Province>(dto.Province, ignoreCase: true),
            dto.District,
            dto.Municipality,
            dto.WardNumber,
            dto.StreetOrTole,
            dto.Landmark,
            dto.PostalCode);

    public static AddressDto ToDto(this NepalAddress address) =>
        new(
            address.Province.ToString(),
            address.District,
            address.Municipality,
            address.WardNumber,
            address.StreetOrTole,
            address.Landmark,
            address.PostalCode);
}
