using NepalParcel.Application.Branches;

namespace NepalParcel.UnitTests.Application;

public sealed class CreateBranchRequestValidatorTests
{
    private readonly CreateBranchRequestValidator _validator = new();

    [Fact]
    public void ValidRequest_Passes()
    {
        Assert.True(_validator.Validate(TestData.ValidCreateBranchRequest()).IsValid);
    }

    [Fact]
    public void InvalidFields_ReportEachProperty()
    {
        var request = TestData.ValidCreateBranchRequest("KTM-01") with
        {
            Type = "Kiosk",
            PhoneNumber = "123",
            Email = "not-an-email",
            Address = TestData.KathmanduAddressDto() with { Province = "Province1", WardNumber = 0, PostalCode = "123" },
        };

        var result = _validator.Validate(request);

        var failed = result.Errors.Select(e => e.PropertyName).ToHashSet();
        Assert.Contains("Code", failed);
        Assert.Contains("Type", failed);
        Assert.Contains("PhoneNumber", failed);
        Assert.Contains("Email", failed);
        Assert.Contains("Address.Province", failed);
        Assert.Contains("Address.WardNumber", failed);
        Assert.Contains("Address.PostalCode", failed);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("bagmati")]
    public void Province_IsCaseInsensitiveName_NotNumber(string province)
    {
        var request = TestData.ValidCreateBranchRequest() with
        {
            Address = TestData.KathmanduAddressDto() with { Province = province },
        };

        var isNumeric = int.TryParse(province, out _);
        Assert.Equal(!isNumeric, _validator.Validate(request).IsValid);
    }
}
