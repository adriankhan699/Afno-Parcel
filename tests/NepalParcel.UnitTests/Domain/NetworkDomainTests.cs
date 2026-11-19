using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Warehouses;

namespace NepalParcel.UnitTests.Domain;

public sealed class StaffAssignmentTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid Branch = Guid.NewGuid();
    private static readonly Guid Warehouse = Guid.NewGuid();

    [Fact]
    public void Create_StartsActive_AndRemembersWhoAssignedIt()
    {
        var admin = Guid.NewGuid();

        var assignment = StaffAssignment.Create(User, Branch, Warehouse, TestData.Now, admin);

        Assert.True(assignment.IsActive);
        Assert.Equal(User, assignment.UserId);
        Assert.Equal(Branch, assignment.BranchId);
        Assert.Equal(Warehouse, assignment.WarehouseId);
        Assert.Equal(admin, assignment.AssignedByUserId);
        Assert.Null(assignment.EndedAtUtc);
    }

    [Fact]
    public void Create_RequiresAPersonAndABranch()
    {
        Assert.Throws<DomainException>(() => StaffAssignment.Create(Guid.Empty, Branch, null, TestData.Now, null));
        Assert.Throws<DomainException>(() => StaffAssignment.Create(User, Guid.Empty, null, TestData.Now, null));
    }

    [Fact]
    public void End_ClosesTheAssignment_ButOnlyOnce()
    {
        var assignment = StaffAssignment.Create(User, Branch, null, TestData.Now, null);
        var by = Guid.NewGuid();

        assignment.End(TestData.Now.AddDays(1), by);

        Assert.False(assignment.IsActive);
        Assert.Equal(TestData.Now.AddDays(1), assignment.EndedAtUtc);
        Assert.Equal(by, assignment.EndedByUserId);
        Assert.Throws<DomainException>(() => assignment.End(TestData.Now.AddDays(2), by));
    }

    [Fact]
    public void ABranchWideAssignment_CoversEveryWarehouseOfThatBranch_AndNoOthers()
    {
        var assignment = StaffAssignment.Create(User, Branch, null, TestData.Now, null);

        Assert.True(assignment.CoversWarehouse(Branch, Warehouse));
        Assert.True(assignment.CoversWarehouse(Branch, Guid.NewGuid()));
        Assert.False(assignment.CoversWarehouse(Guid.NewGuid(), Warehouse));
    }

    [Fact]
    public void AWarehouseAssignment_CoversOnlyThatWarehouse()
    {
        var assignment = StaffAssignment.Create(User, Branch, Warehouse, TestData.Now, null);

        Assert.True(assignment.CoversWarehouse(Branch, Warehouse));
        Assert.False(assignment.CoversWarehouse(Branch, Guid.NewGuid()));
    }

    [Fact]
    public void AnEndedAssignment_CoversNothing()
    {
        var assignment = StaffAssignment.Create(User, Branch, null, TestData.Now, null);
        assignment.End(TestData.Now.AddHours(1), null);

        Assert.False(assignment.CoversWarehouse(Branch, Warehouse));
    }
}

public sealed class ParcelMovementTests
{
    private static readonly Guid Parcel = Guid.NewGuid();
    private static readonly Guid Hub = Guid.NewGuid();
    private static readonly Guid Staff = Guid.NewGuid();

    [Fact]
    public void Create_KeepsEveryFieldTheLedgerNeeds()
    {
        var other = Guid.NewGuid();

        var movement = ParcelMovement.Create(Parcel, Hub, ScanType.Dispatch, Staff, "Hari", TestData.Now, "Loaded on truck 4", other);

        Assert.Equal(Parcel, movement.ParcelId);
        Assert.Equal(Hub, movement.WarehouseId);
        Assert.Equal(ScanType.Dispatch, movement.ScanType);
        Assert.Equal(Staff, movement.StaffUserId);
        Assert.Equal("Hari", movement.StaffName);
        Assert.Equal(TestData.Now, movement.OccurredAtUtc);
        Assert.Equal("Loaded on truck 4", movement.Remarks);
        Assert.Equal(other, movement.ToWarehouseId);
        Assert.False(movement.IsUnexpectedArrival);
    }

    [Fact]
    public void Create_OnlyADispatchCanNameADestination_AndNotTheSameWarehouse()
    {
        Assert.Throws<DomainException>(() => ParcelMovement.Create(Parcel, Hub, ScanType.Arrival, Staff, null, TestData.Now, toWarehouseId: Guid.NewGuid()));
        Assert.Throws<DomainException>(() => ParcelMovement.Create(Parcel, Hub, ScanType.Dispatch, Staff, null, TestData.Now, toWarehouseId: Hub));
    }

    [Fact]
    public void Create_OnlyAnArrivalCanBeUnexpected()
    {
        var arrival = ParcelMovement.Create(Parcel, Hub, ScanType.Arrival, Staff, null, TestData.Now, isUnexpectedArrival: true);

        Assert.True(arrival.IsUnexpectedArrival);
        Assert.Throws<DomainException>(() => ParcelMovement.Create(Parcel, Hub, ScanType.Dispatch, Staff, null, TestData.Now, isUnexpectedArrival: true));
    }

    [Fact]
    public void Create_RejectsMissingIdsUnknownTypesAndLongRemarks()
    {
        Assert.Throws<DomainException>(() => ParcelMovement.Create(Guid.Empty, Hub, ScanType.Arrival, Staff, null, TestData.Now));
        Assert.Throws<DomainException>(() => ParcelMovement.Create(Parcel, Guid.Empty, ScanType.Arrival, Staff, null, TestData.Now));
        Assert.Throws<DomainException>(() => ParcelMovement.Create(Parcel, Hub, ScanType.Arrival, Guid.Empty, null, TestData.Now));
        Assert.Throws<DomainException>(() => ParcelMovement.Create(Parcel, Hub, (ScanType)99, Staff, null, TestData.Now));
        Assert.Throws<DomainException>(() => ParcelMovement.Create(Parcel, Hub, ScanType.Arrival, Staff, null, TestData.Now, new string('x', 501)));
    }

    [Fact]
    public void AScan_RecordsParcelWarehouseStaffTypeTimeAndRemarks_AndPointsAtItsMovement()
    {
        var movementId = Guid.NewGuid();

        var scan = ParcelScan.Create(Parcel, Hub, Staff, "Hari", ScanType.Arrival, TestData.Now, ScanOutcome.Duplicate, movementId, "again");

        Assert.Equal(Parcel, scan.ParcelId);
        Assert.Equal(Hub, scan.WarehouseId);
        Assert.Equal(Staff, scan.StaffUserId);
        Assert.Equal(ScanType.Arrival, scan.ScanType);
        Assert.Equal(TestData.Now, scan.ScannedAtUtc);
        Assert.Equal("again", scan.Remarks);
        Assert.Equal(ScanOutcome.Duplicate, scan.Outcome);
        Assert.Equal(movementId, scan.MovementId);
    }

    [Fact]
    public void AScan_RejectsMissingIdsAndUnknownValues()
    {
        var movement = Guid.NewGuid();

        Assert.Throws<DomainException>(() => ParcelScan.Create(Guid.Empty, Hub, Staff, null, ScanType.Arrival, TestData.Now, ScanOutcome.Applied, movement));
        Assert.Throws<DomainException>(() => ParcelScan.Create(Parcel, Hub, Staff, null, ScanType.Arrival, TestData.Now, ScanOutcome.Applied, Guid.Empty));
        Assert.Throws<DomainException>(() => ParcelScan.Create(Parcel, Hub, Staff, null, (ScanType)0, TestData.Now, ScanOutcome.Applied, movement));
        Assert.Throws<DomainException>(() => ParcelScan.Create(Parcel, Hub, Staff, null, ScanType.Arrival, TestData.Now, (ScanOutcome)7, movement));
    }
}

public sealed class TrackingNumberExtractionTests
{
    [Fact]
    public void TryExtract_AcceptsTheBareNumber_InAnyCaseWithSpacesOrDashes()
    {
        var number = TrackingNumbers.Generate();

        Assert.True(TrackingNumbers.TryExtract(number, out var plain));
        Assert.True(TrackingNumbers.TryExtract($" {number[..3].ToLowerInvariant()}-{number[3..].ToLowerInvariant()} ", out var typed));
        Assert.Equal(number, plain);
        Assert.Equal(number, typed);
    }

    [Theory]
    [InlineData("https://afno.example/track/{0}")]
    [InlineData("http://localhost:5173/track/{0}?utm=qr")]
    [InlineData("TRACK:{0};")]
    public void TryExtract_FindsTheNumberInsideATrackingLink(string template)
    {
        var number = TrackingNumbers.Generate();

        Assert.True(TrackingNumbers.TryExtract(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, number.ToLowerInvariant()), out var found));
        Assert.Equal(number, found);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello world")]
    [InlineData("AFN")]
    [InlineData("AFNAAAAAAAAAAB")]
    [InlineData("https://afno.example/track/AFN-nothing-here")]
    public void TryExtract_RejectsEverythingElse(string? text)
    {
        Assert.False(TrackingNumbers.TryExtract(text, out var found));
        Assert.Equal(string.Empty, found);
    }

    [Fact]
    public void TryExtract_RejectsANumberWithAWrongCheckCharacter()
    {
        var number = TrackingNumbers.Generate();
        var tampered = number[..^1] + (number[^1] == '2' ? '3' : '2');

        Assert.False(TrackingNumbers.TryExtract(tampered, out _));
        Assert.False(TrackingNumbers.TryExtract($"https://afno.example/track/{tampered}", out _));
    }
}

public sealed class ParcelWarehouseStateTests
{
    [Fact]
    public void ChangingStatus_StampsWhenItChanged_AndANewParcelStartsAtItsBookingTime()
    {
        var parcel = TestData.NewParcel();
        Assert.Equal(TestData.Now, parcel.StatusChangedAtUtc);

        parcel.ChangeStatus(ParcelStatusCode.PickedUp, TestData.Now.AddHours(2), Guid.NewGuid(), "Staff");

        Assert.Equal(TestData.Now.AddHours(2), parcel.StatusChangedAtUtc);
    }

    [Fact]
    public void ANextWarehouse_CanOnlyBeSetWhileInTransit_AndIsClearedWhenTheParcelArrives()
    {
        var destination = Guid.NewGuid();
        var parcel = ParcelTests.ParcelAt(ParcelStatusCode.ArrivedAtWarehouse);
        Assert.Throws<DomainException>(() => parcel.SetNextWarehouse(destination));

        parcel.ChangeStatus(ParcelStatusCode.InTransit, TestData.Now.AddDays(1), Guid.NewGuid(), "Staff");
        parcel.SetNextWarehouse(destination);
        Assert.Equal(destination, parcel.NextWarehouseId);

        parcel.ChangeStatus(ParcelStatusCode.ArrivedAtWarehouse, TestData.Now.AddDays(2), Guid.NewGuid(), "Staff", warehouseId: Guid.NewGuid());
        Assert.Null(parcel.NextWarehouseId);
    }

    [Fact]
    public void ClearingTheNextWarehouse_IsAlwaysAllowed()
    {
        var parcel = ParcelTests.ParcelAt(ParcelStatusCode.ArrivedAtWarehouse);

        parcel.SetNextWarehouse(null);

        Assert.Null(parcel.NextWarehouseId);
    }
}
