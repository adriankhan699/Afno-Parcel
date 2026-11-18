using NepalParcel.Domain.Common;

namespace NepalParcel.UnitTests.Domain;

public sealed class NepalAddressTests
{
    [Fact]
    public void Create_TrimsValues_AndTreatsBlankOptionalsAsNull()
    {
        var address = NepalAddress.Create(Province.Gandaki, "  Kaski ", " Pokhara Metropolitan City ", 6, "  ", null, "");

        Assert.Equal("Kaski", address.District);
        Assert.Equal("Pokhara Metropolitan City", address.Municipality);
        Assert.Null(address.StreetOrTole);
        Assert.Null(address.PostalCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(36)]
    public void Create_WardOutOfRange_Throws(int ward)
    {
        Assert.Throws<DomainException>(() => NepalAddress.Create(Province.Bagmati, "Lalitpur", "Lalitpur Metropolitan City", ward));
    }

    [Theory]
    [InlineData("4460")]
    [InlineData("44A00")]
    public void Create_InvalidPostalCode_Throws(string postalCode)
    {
        Assert.Throws<DomainException>(() =>
            NepalAddress.Create(Province.Bagmati, "Lalitpur", "Lalitpur Metropolitan City", 3, postalCode: postalCode));
    }

    [Fact]
    public void Create_UndefinedProvince_Throws()
    {
        Assert.Throws<DomainException>(() => NepalAddress.Create((Province)99, "X", "Y", 1));
    }

    [Fact]
    public void Create_MissingDistrict_Throws()
    {
        Assert.Throws<DomainException>(() => NepalAddress.Create(Province.Koshi, " ", "Biratnagar", 1));
    }

    [Fact]
    public void ToString_FormatsNepaliAddressOrder()
    {
        Assert.Equal(
            "New Baneshwor, Kathmandu Metropolitan City-10, Kathmandu, Bagmati Province, 44600",
            TestData.KathmanduAddress().ToString());
    }
}
