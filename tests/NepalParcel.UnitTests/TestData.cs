using NepalParcel.Contracts.Branches;
using NepalParcel.Contracts.Common;
using NepalParcel.Domain.Common;

namespace NepalParcel.UnitTests;

internal static class TestData
{
    public static NepalAddress KathmanduAddress() =>
        NepalAddress.Create(Province.Bagmati, "Kathmandu", "Kathmandu Metropolitan City", 10, "New Baneshwor", "Near Everest Hotel", "44600");

    public static AddressDto KathmanduAddressDto() =>
        new("Bagmati", "Kathmandu", "Kathmandu Metropolitan City", 10, "New Baneshwor", null, "44600");

    public static CreateBranchRequest ValidCreateBranchRequest(string code = "KTM01") =>
        new(code, "Kathmandu Main Branch", "Branch", KathmanduAddressDto(), "9841000000", "ktm@nepalparcel.test");
}
