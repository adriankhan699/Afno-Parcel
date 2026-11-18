using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Common;

namespace NepalParcel.UnitTests.Domain;

public sealed class BranchTests
{
    [Fact]
    public void Create_NormalizesValues_AndIsActive()
    {
        var branch = Branch.Create(" ktm01 ", " Kathmandu ", BranchType.Branch, TestData.KathmanduAddress(), "+977-9841000000", "KTM@NepalParcel.test");

        Assert.NotEqual(Guid.Empty, branch.Id);
        Assert.Equal("KTM01", branch.Code);
        Assert.Equal("Kathmandu", branch.Name);
        Assert.Equal("9841000000", branch.PhoneNumber);
        Assert.Equal("ktm@nepalparcel.test", branch.Email);
        Assert.True(branch.IsActive);
    }

    [Theory]
    [InlineData("KTM-01")]
    [InlineData("TOOLONGCODE1")]
    [InlineData("")]
    public void Create_InvalidCode_Throws(string code)
    {
        Assert.Throws<DomainException>(() =>
            Branch.Create(code, "Kathmandu", BranchType.Branch, TestData.KathmanduAddress(), "9841000000"));
    }

    [Fact]
    public void Create_InvalidPhone_Throws()
    {
        Assert.Throws<DomainException>(() =>
            Branch.Create("KTM01", "Kathmandu", BranchType.Branch, TestData.KathmanduAddress(), "12345"));
    }

    [Fact]
    public void Deactivate_And_Activate_ToggleState()
    {
        var branch = Branch.Create("PKR01", "Pokhara Branch", BranchType.Branch, TestData.KathmanduAddress(), "9851000000");

        branch.Deactivate();
        Assert.False(branch.IsActive);

        branch.Activate();
        Assert.True(branch.IsActive);
    }
}
