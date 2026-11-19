using FluentValidation.Results;
using NepalParcel.Application.Tracking;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Tracking;
using NepalParcel.Contracts.Warehouses;
using NepalParcel.Domain.Common;

namespace NepalParcel.UnitTests.Application;

public sealed class TrackingValidatorsTests
{
    private static string[] Failed(ValidationResult result) => result.Errors.Select(e => e.PropertyName).Distinct().ToArray();

    [Theory]
    [InlineData("PickedUp")]
    [InlineData("shipped")]
    [InlineData("InTransit")]
    [InlineData("ArrivedAtWarehouse")]
    [InlineData("OutForDelivery")]
    [InlineData("Delivered")]
    [InlineData("DeliveryFailed")]
    [InlineData("Returned")]
    public void StatusUpdate_AcceptsEverySettableStatus(string status) =>
        Assert.True(new UpdateParcelStatusRequestValidator().Validate(new UpdateParcelStatusRequest(status)).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("BookingCreated")]
    [InlineData("Cancelled")]
    [InlineData("Booked")]
    [InlineData("ReceivedAtBranch")]
    [InlineData("4")]
    [InlineData("Flying")]
    public void StatusUpdate_RejectsAnythingElse(string status) =>
        Assert.Contains("Status", Failed(new UpdateParcelStatusRequestValidator().Validate(new UpdateParcelStatusRequest(status))));

    [Fact]
    public void StatusUpdate_LimitsRemarks_AndRejectsEmptyIds()
    {
        var validator = new UpdateParcelStatusRequestValidator();

        Assert.True(validator.Validate(new UpdateParcelStatusRequest("PickedUp", new string('x', 500))).IsValid);
        Assert.Contains("Remarks", Failed(validator.Validate(new UpdateParcelStatusRequest("PickedUp", new string('x', 501)))));
        Assert.Contains("BranchId", Failed(validator.Validate(new UpdateParcelStatusRequest("Shipped", BranchId: Guid.Empty))));
        Assert.Contains("WarehouseId", Failed(validator.Validate(new UpdateParcelStatusRequest("ArrivedAtWarehouse", WarehouseId: Guid.Empty))));
    }

    [Theory]
    [InlineData("Arrival", true)]
    [InlineData("dispatch", true)]
    [InlineData("OutForDelivery", true)]
    [InlineData("Delivered", false)]
    [InlineData("1", false)]
    [InlineData("", false)]
    public void Scan_AcceptsOnlyTheThreeScanTypes(string scanType, bool valid)
    {
        var result = new ScanRequestValidator().Validate(new ScanRequest("AFNK7M2Q9XT4B3", scanType, Guid.NewGuid()));

        Assert.Equal(valid, result.IsValid);
    }

    [Fact]
    public void Scan_NeedsATrackingNumberAndAWarehouse()
    {
        var validator = new ScanRequestValidator();

        Assert.Contains("TrackingNumber", Failed(validator.Validate(new ScanRequest("", "Arrival", Guid.NewGuid()))));
        Assert.Contains("TrackingNumber", Failed(validator.Validate(new ScanRequest(new string('A', 41), "Arrival", Guid.NewGuid()))));
        Assert.Contains("WarehouseId", Failed(validator.Validate(new ScanRequest("AFNK7M2Q9XT4B3", "Arrival", Guid.Empty))));
    }

    [Fact]
    public void BatchScan_AcceptsOneToOneHundredNumbers()
    {
        var validator = new BatchScanRequestValidator();
        var warehouse = Guid.NewGuid();

        Assert.True(validator.Validate(new BatchScanRequest("Arrival", warehouse, ["AFNK7M2Q9XT4B3"])).IsValid);
        Assert.True(validator.Validate(new BatchScanRequest("Arrival", warehouse, Enumerable.Repeat("AFNK7M2Q9XT4B3", 100).ToList())).IsValid);
        Assert.Contains("TrackingNumbers", Failed(validator.Validate(new BatchScanRequest("Arrival", warehouse, []))));
        Assert.Contains("TrackingNumbers", Failed(validator.Validate(new BatchScanRequest("Arrival", warehouse, Enumerable.Repeat("AFNK7M2Q9XT4B3", 101).ToList()))));
        Assert.Contains("ScanType", Failed(validator.Validate(new BatchScanRequest("Teleport", warehouse, ["AFNK7M2Q9XT4B3"]))));
        Assert.Contains("WarehouseId", Failed(validator.Validate(new BatchScanRequest("Arrival", Guid.Empty, ["AFNK7M2Q9XT4B3"]))));
    }

    [Theory]
    [InlineData("123456", true)]
    [InlineData("000000", true)]
    [InlineData("12345", false)]
    [InlineData("1234567", false)]
    [InlineData("12345a", false)]
    [InlineData(" 12345", false)]
    [InlineData("", false)]
    public void VerifyCode_MustBeSixDigits(string code, bool valid) =>
        Assert.Equal(valid, new VerifyTrackingOtpRequestValidator().Validate(new VerifyTrackingOtpRequest(code)).IsValid);

    [Fact]
    public void Warehouse_NeedsValidDetails()
    {
        var validator = new CreateWarehouseRequestValidator();
        var valid = new CreateWarehouseRequest(Guid.NewGuid(), "KTMW1", "Kathmandu Hub", TestData.KathmanduAddressDto(), "01-4123456", 5000);

        Assert.True(validator.Validate(valid).IsValid);
        Assert.Contains("BranchId", Failed(validator.Validate(valid with { BranchId = Guid.Empty })));
        Assert.Contains("Code", Failed(validator.Validate(valid with { Code = "KTM-W1" })));
        Assert.Contains("Code", Failed(validator.Validate(valid with { Code = "ABCDEFGHIJK" })));
        Assert.Contains("Name", Failed(validator.Validate(valid with { Name = "" })));
        Assert.Contains("PhoneNumber", Failed(validator.Validate(valid with { PhoneNumber = "123" })));
        Assert.Contains("CapacityParcels", Failed(validator.Validate(valid with { CapacityParcels = 0 })));
        Assert.Contains("Address.District", Failed(validator.Validate(valid with { Address = TestData.KathmanduAddressDto() with { District = "Atlantis" } })));
        Assert.True(validator.Validate(valid with { CapacityParcels = null }).IsValid);
    }
}
