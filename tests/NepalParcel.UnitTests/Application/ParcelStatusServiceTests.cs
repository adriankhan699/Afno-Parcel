using Microsoft.Extensions.Logging.Abstractions;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Branches;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Parcels;
using NepalParcel.Application.Tracking;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Tracking;
using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Warehouses;
using NepalParcel.UnitTests.Domain;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace NepalParcel.UnitTests.Application;

public sealed class ParcelStatusServiceTests
{
    private readonly IParcelRepository _parcels = Substitute.For<IParcelRepository>();
    private readonly IBranchRepository _branches = Substitute.For<IBranchRepository>();
    private readonly IWarehouseRepository _warehouses = Substitute.For<IWarehouseRepository>();
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IClientInfo _client = Substitute.For<IClientInfo>();
    private readonly FixedClock _clock = new(TestData.Now.AddDays(10));
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Branch _branch = Branch.Create("KTM01", "Kathmandu Main", BranchType.Branch, TestData.KathmanduAddress(), "9841000000");
    private readonly Warehouse _warehouse;
    private readonly ParcelStatusService _sut;

    public ParcelStatusServiceTests()
    {
        _warehouse = Warehouse.Create(_branch.Id, "KTMW1", "Kathmandu Hub", TestData.KathmanduAddress(), "9841000001");
        _currentUser.UserId.Returns(_userId);
        _currentUser.UserName.Returns("Hari Staff");
        _branches.GetByIdAsync(_branch.Id, Arg.Any<CancellationToken>()).Returns(_branch);
        _warehouses.GetByIdAsync(_warehouse.Id, Arg.Any<CancellationToken>()).Returns(_warehouse);

        _sut = new ParcelStatusService(
            _parcels, _branches, _warehouses, _audit, _unitOfWork, _currentUser, _client, _clock,
            new UpdateParcelStatusRequestValidator(),
            new ScanRequestValidator(),
            new BatchScanRequestValidator(),
            NullLogger<ParcelStatusService>.Instance);
    }

    private void SignInAs(string role)
    {
        _currentUser.IsInRole(Arg.Any<string>()).Returns(false);
        _currentUser.IsInRole(role).Returns(true);
    }

    private Parcel Stored(ParcelStatusCode at = ParcelStatusCode.BookingCreated)
    {
        var parcel = ParcelTests.ParcelAt(at);
        _parcels.GetByIdAsync(parcel.Id, Arg.Any<CancellationToken>()).Returns(parcel);
        _parcels.GetByTrackingNumberAsync(parcel.TrackingNumber, Arg.Any<CancellationToken>()).Returns(parcel);
        return parcel;
    }

    // ---- updating by id ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_AppendsAHistoryRow_WithPreviousAndNewStatus_Staff_Place_Time_AndRemarks()
    {
        SignInAs(Roles.WarehouseStaff);
        var parcel = Stored(ParcelStatusCode.InTransit);

        var response = await _sut.UpdateAsync(
            parcel.Id,
            new UpdateParcelStatusRequest("ArrivedAtWarehouse", "Checked and sorted", WarehouseId: _warehouse.Id));

        var row = parcel.StatusHistory.OrderBy(h => h.OccurredAtUtc).Last();
        Assert.Equal((int)ParcelStatusCode.InTransit, row.FromStatusId);
        Assert.Equal((int)ParcelStatusCode.ArrivedAtWarehouse, row.ToStatusId);
        Assert.Equal(_userId, row.ChangedByUserId);
        Assert.Equal("Hari Staff", row.ChangedByName);
        Assert.Equal(_warehouse.Id, row.WarehouseId);
        Assert.Null(row.BranchId);
        Assert.Equal(_clock.GetUtcNow(), row.OccurredAtUtc);
        Assert.Equal("Checked and sorted", row.Remarks);

        Assert.Equal("InTransit", response.PreviousStatus);
        Assert.Equal("Arrived at warehouse", response.StatusName);
        Assert.Equal("Kathmandu Hub", response.Location);
        Assert.Equal(parcel.TrackingNumber, response.TrackingNumber);
        Assert.DoesNotContain("Cancelled", response.NextStatuses);
        Assert.Contains("OutForDelivery", response.NextStatuses);
        _audit.Received(1).Add(Arg.Is<AuditLog>(a => a.Action == AuditActions.ParcelStatusChanged && a.UserId == _userId));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_NeverTouchesExistingHistory_OnlyAddsToIt()
    {
        SignInAs(Roles.BookingStaff);
        var parcel = Stored();
        var before = parcel.StatusHistory.ToList();

        await _sut.UpdateAsync(parcel.Id, new UpdateParcelStatusRequest("PickedUp"));

        Assert.Equal(before.Count + 1, parcel.StatusHistory.Count);
        Assert.All(before, original => Assert.Contains(original, parcel.StatusHistory));
        Assert.Equal(before[0].ToStatusId, parcel.StatusHistory.First().ToStatusId);
        Assert.Equal(before[0].OccurredAtUtc, parcel.StatusHistory.First().OccurredAtUtc);
    }

    [Theory]
    [InlineData(Roles.Customer, "PickedUp")]
    [InlineData(Roles.Accountant, "PickedUp")]
    [InlineData(Roles.WarehouseStaff, "PickedUp")]
    [InlineData(Roles.DeliveryAgent, "Shipped")]
    [InlineData(Roles.BookingStaff, "InTransit")]
    [InlineData(Roles.WarehouseStaff, "Delivered")]
    public async Task Update_ByARoleThatMayNotMakeThatChange_IsForbidden_BeforeTheParcelIsEvenLoaded(string role, string status)
    {
        SignInAs(role);
        var parcel = Stored();

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            _sut.UpdateAsync(parcel.Id, new UpdateParcelStatusRequest(status, "reason", BranchId: _branch.Id)));

        await _parcels.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Single(parcel.StatusHistory);
    }

    [Fact]
    public async Task Update_Anonymous_IsUnauthorized()
    {
        _currentUser.UserId.Returns((Guid?)null);
        var parcel = Stored();

        await Assert.ThrowsAsync<UnauthorizedException>(() => _sut.UpdateAsync(parcel.Id, new UpdateParcelStatusRequest("PickedUp")));
    }

    [Fact]
    public async Task Update_InvalidTransition_IsRefusedWithAnExplanation_AndNothingChanges()
    {
        SignInAs(Roles.SuperAdmin);
        var parcel = Stored();

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _sut.UpdateAsync(parcel.Id, new UpdateParcelStatusRequest("Delivered")));

        Assert.Contains("cannot move to", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ParcelStatusCode.BookingCreated, parcel.Status);
        Assert.Single(parcel.StatusHistory);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        _audit.DidNotReceive().Add(Arg.Any<AuditLog>());
    }

    [Theory]
    [InlineData("Cancelled")]
    [InlineData("BookingCreated")]
    [InlineData("Teleported")]
    [InlineData("")]
    [InlineData("3")]
    public async Task Update_ToAStatusThatCannotBeSetThisWay_FailsValidation(string status)
    {
        SignInAs(Roles.SuperAdmin);
        var parcel = Stored();

        var ex = await Assert.ThrowsAsync<RequestValidationException>(() => _sut.UpdateAsync(parcel.Id, new UpdateParcelStatusRequest(status)));

        Assert.Contains("Status", ex.Errors.Keys);
    }

    [Fact]
    public async Task Update_Cancelling_PointsToTheCancelEndpoint()
    {
        SignInAs(Roles.SuperAdmin);
        var parcel = Stored();

        var ex = await Assert.ThrowsAsync<RequestValidationException>(() => _sut.UpdateAsync(parcel.Id, new UpdateParcelStatusRequest("Cancelled", "x")));

        Assert.Contains("cancel endpoint", ex.Errors["Status"][0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_FailedDelivery_NeedsRemarks()
    {
        SignInAs(Roles.DeliveryAgent);
        var parcel = Stored(ParcelStatusCode.OutForDelivery);

        await Assert.ThrowsAsync<DomainException>(() => _sut.UpdateAsync(parcel.Id, new UpdateParcelStatusRequest("DeliveryFailed")));
        var ok = await _sut.UpdateAsync(parcel.Id, new UpdateParcelStatusRequest("DeliveryFailed", "Receiver not at home"));

        Assert.Equal("DeliveryFailed", ok.Status);
    }

    [Fact]
    public async Task Update_ShippedWithoutABranch_IsRefused_AndWithABranchRecordsIt()
    {
        SignInAs(Roles.BookingStaff);
        var parcel = Stored(ParcelStatusCode.PickedUp);

        await Assert.ThrowsAsync<DomainException>(() => _sut.UpdateAsync(parcel.Id, new UpdateParcelStatusRequest("Shipped")));
        var ok = await _sut.UpdateAsync(parcel.Id, new UpdateParcelStatusRequest("shipped", BranchId: _branch.Id));

        Assert.Equal("Kathmandu Main", ok.Location);
        Assert.Equal(_branch.Id, parcel.StatusHistory.OrderBy(h => h.OccurredAtUtc).Last().BranchId);
    }

    [Fact]
    public async Task Update_UnknownParcel_ThrowsNotFound()
    {
        SignInAs(Roles.SuperAdmin);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.UpdateAsync(Guid.NewGuid(), new UpdateParcelStatusRequest("PickedUp")));
    }

    [Fact]
    public async Task Update_UnknownBranch_ThrowsNotFound_AndInactiveBranchOrWarehouseIsRefused()
    {
        SignInAs(Roles.SuperAdmin);
        var parcel = Stored(ParcelStatusCode.PickedUp);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.UpdateAsync(parcel.Id, new UpdateParcelStatusRequest("Shipped", BranchId: Guid.NewGuid())));

        _branch.Deactivate();
        await Assert.ThrowsAsync<DomainException>(() =>
            _sut.UpdateAsync(parcel.Id, new UpdateParcelStatusRequest("Shipped", BranchId: _branch.Id)));

        _warehouse.Deactivate();
        var inTransit = Stored(ParcelStatusCode.InTransit);
        await Assert.ThrowsAsync<DomainException>(() =>
            _sut.UpdateAsync(inTransit.Id, new UpdateParcelStatusRequest("ArrivedAtWarehouse", WarehouseId: _warehouse.Id)));
        Assert.Equal(ParcelStatusCode.PickedUp, parcel.Status);
        Assert.Equal(ParcelStatusCode.InTransit, inTransit.Status);
    }

    [Fact]
    public async Task Update_ByTrackingNumber_AcceptsLowerCaseAndDashes()
    {
        SignInAs(Roles.BookingStaff);
        var parcel = Stored();
        var typed = parcel.TrackingNumber.ToLowerInvariant().Insert(3, "-");

        var response = await _sut.UpdateByTrackingNumberAsync(typed, new UpdateParcelStatusRequest("PickedUp"));

        Assert.Equal(parcel.Id, response.ParcelId);
        Assert.Equal(ParcelStatusCode.PickedUp, parcel.Status);
    }

    [Fact]
    public async Task Update_ByTrackingNumber_MalformedOrUnknown_ThrowsNotFound()
    {
        SignInAs(Roles.BookingStaff);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.UpdateByTrackingNumberAsync("nonsense", new UpdateParcelStatusRequest("PickedUp")));
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.UpdateByTrackingNumberAsync(TrackingNumbers.Generate(), new UpdateParcelStatusRequest("PickedUp")));
    }

    [Fact]
    public async Task Update_WhenSomeoneElseChangedTheParcelFirst_SurfacesTheConflict()
    {
        SignInAs(Roles.BookingStaff);
        var parcel = Stored();
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new ConcurrencyException());

        await Assert.ThrowsAsync<ConcurrencyException>(() => _sut.UpdateAsync(parcel.Id, new UpdateParcelStatusRequest("PickedUp")));
    }

    // ---- scanning ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Scan_Arrival_RecordsArrivedAtWarehouse_AtThatWarehouse()
    {
        SignInAs(Roles.WarehouseStaff);
        var parcel = Stored(ParcelStatusCode.InTransit);

        var response = await _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Arrival", _warehouse.Id, "Scanned at dock 2"));

        Assert.Equal("ArrivedAtWarehouse", response.Status);
        Assert.Equal("Kathmandu Hub", response.Location);
        var row = parcel.StatusHistory.OrderBy(h => h.OccurredAtUtc).Last();
        Assert.Equal(_warehouse.Id, row.WarehouseId);
        Assert.Equal(_userId, row.ChangedByUserId);
        Assert.Equal("Scanned at dock 2", row.Remarks);
    }

    [Theory]
    [InlineData("Dispatch", ParcelStatusCode.ArrivedAtWarehouse, "InTransit")]
    [InlineData("OutForDelivery", ParcelStatusCode.ArrivedAtWarehouse, "OutForDelivery")]
    [InlineData("arrival", ParcelStatusCode.InTransit, "ArrivedAtWarehouse")]
    public async Task Scan_Types_MapToTheRightStatus(string scanType, ParcelStatusCode startAt, string expected)
    {
        SignInAs(Roles.WarehouseStaff);
        var parcel = Stored(startAt);

        var response = await _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, scanType, _warehouse.Id));

        Assert.Equal(expected, response.Status);
    }

    [Fact]
    public async Task Scan_ScanningTheSameParcelTwice_FailsTheSecondTime()
    {
        SignInAs(Roles.WarehouseStaff);
        var parcel = Stored(ParcelStatusCode.InTransit);
        await _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Arrival", _warehouse.Id));
        var rowsAfterFirstScan = parcel.StatusHistory.Count;

        var ex = await Assert.ThrowsAsync<DomainException>(() => _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Arrival", _warehouse.Id)));

        Assert.Contains("cannot move to", ex.Message, StringComparison.Ordinal);
        Assert.Equal(rowsAfterFirstScan, parcel.StatusHistory.Count);
    }

    [Fact]
    public async Task Scan_ByCounterStaff_IsForbidden()
    {
        SignInAs(Roles.BookingStaff);
        var parcel = Stored(ParcelStatusCode.InTransit);

        await Assert.ThrowsAsync<ForbiddenException>(() => _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Arrival", _warehouse.Id)));
    }

    [Fact]
    public async Task Scan_UnknownTrackingNumber_UnknownWarehouse_AndBadType_AreRefused()
    {
        SignInAs(Roles.WarehouseStaff);
        var parcel = Stored(ParcelStatusCode.InTransit);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.ScanAsync(new ScanRequest(TrackingNumbers.Generate(), "Arrival", _warehouse.Id)));
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Arrival", Guid.NewGuid())));
        await Assert.ThrowsAsync<RequestValidationException>(() => _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Teleport", _warehouse.Id)));
        Assert.Equal(ParcelStatusCode.InTransit, parcel.Status);
    }

    // ---- batch scanning ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Batch_EachParcelSucceedsOrFailsOnItsOwn()
    {
        SignInAs(Roles.WarehouseStaff);
        var good1 = Stored(ParcelStatusCode.InTransit);
        var good2 = Stored(ParcelStatusCode.Shipped);
        var wrongState = Stored(ParcelStatusCode.PickedUp); // cannot arrive at a warehouse before it is shipped
        var unknown = TrackingNumbers.Generate();

        var result = await _sut.ScanBatchAsync(new BatchScanRequest(
            "Arrival",
            _warehouse.Id,
            [good1.TrackingNumber, wrongState.TrackingNumber, unknown, "not-a-number", good2.TrackingNumber]));

        Assert.Equal(2, result.Succeeded);
        Assert.Equal(3, result.Failed);
        Assert.Equal(5, result.Results.Count);
        Assert.True(result.Results.Single(r => r.TrackingNumber == good1.TrackingNumber).Succeeded);
        Assert.True(result.Results.Single(r => r.TrackingNumber == good2.TrackingNumber).Succeeded);
        Assert.Contains("cannot move to", result.Results.Single(r => r.TrackingNumber == wrongState.TrackingNumber).Error, StringComparison.Ordinal);
        Assert.Contains("No parcel", result.Results.Single(r => r.TrackingNumber == unknown).Error, StringComparison.Ordinal);
        Assert.False(result.Results.Single(r => r.TrackingNumber == "NOTANUMBER").Succeeded);

        Assert.Equal(ParcelStatusCode.ArrivedAtWarehouse, good1.Status);
        Assert.Equal(ParcelStatusCode.ArrivedAtWarehouse, good2.Status);
        Assert.Equal(ParcelStatusCode.PickedUp, wrongState.Status);
        _unitOfWork.Received(1).DiscardPendingChanges(); // only the parcel whose move was refused had anything staged
    }

    [Fact]
    public async Task Batch_ScansEachParcelOnce_EvenIfItIsListedTwice()
    {
        SignInAs(Roles.WarehouseStaff);
        var parcel = Stored(ParcelStatusCode.InTransit);

        var result = await _sut.ScanBatchAsync(new BatchScanRequest(
            "Arrival", _warehouse.Id, [parcel.TrackingNumber, parcel.TrackingNumber.ToLowerInvariant(), parcel.TrackingNumber]));

        Assert.Single(result.Results);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(1, parcel.StatusHistory.Count(h => h.ToStatusId == (int)ParcelStatusCode.ArrivedAtWarehouse));
    }

    [Fact]
    public async Task Batch_ABlankEntry_FailsValidation()
    {
        SignInAs(Roles.WarehouseStaff);
        var parcel = Stored(ParcelStatusCode.InTransit);

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            _sut.ScanBatchAsync(new BatchScanRequest("Arrival", _warehouse.Id, [parcel.TrackingNumber, "  "])));

        Assert.Equal(ParcelStatusCode.InTransit, parcel.Status);
    }

    [Fact]
    public async Task Batch_ABadWarehouse_FailsTheWholeRequest_BeforeAnythingIsScanned()
    {
        SignInAs(Roles.WarehouseStaff);
        var parcel = Stored(ParcelStatusCode.InTransit);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.ScanBatchAsync(new BatchScanRequest("Arrival", Guid.NewGuid(), [parcel.TrackingNumber])));

        Assert.Equal(ParcelStatusCode.InTransit, parcel.Status);
    }

    [Fact]
    public async Task Batch_ByARoleThatMayNotScan_IsForbidden()
    {
        SignInAs(Roles.DeliveryAgent);
        var parcel = Stored(ParcelStatusCode.InTransit);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            _sut.ScanBatchAsync(new BatchScanRequest("Arrival", _warehouse.Id, [parcel.TrackingNumber])));
    }

    [Fact]
    public async Task Batch_Limits_AreEnforced()
    {
        SignInAs(Roles.WarehouseStaff);

        await Assert.ThrowsAsync<RequestValidationException>(() => _sut.ScanBatchAsync(new BatchScanRequest("Arrival", _warehouse.Id, [])));
        var tooMany = Enumerable.Range(0, 101).Select(_ => TrackingNumbers.Generate()).ToList();
        await Assert.ThrowsAsync<RequestValidationException>(() => _sut.ScanBatchAsync(new BatchScanRequest("Arrival", _warehouse.Id, tooMany)));

        var max = Enumerable.Range(0, 100).Select(_ => TrackingNumbers.Generate()).ToList();
        var result = await _sut.ScanBatchAsync(new BatchScanRequest("Arrival", _warehouse.Id, max));
        Assert.Equal(100, result.Failed);
    }

    [Fact]
    public async Task Batch_AConflictOnOneParcel_DoesNotStopTheRest()
    {
        SignInAs(Roles.WarehouseStaff);
        var first = Stored(ParcelStatusCode.InTransit);
        var second = Stored(ParcelStatusCode.InTransit);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(
            _ => throw new ConcurrencyException(),
            _ => Task.FromResult(1));

        var result = await _sut.ScanBatchAsync(new BatchScanRequest("Arrival", _warehouse.Id, [first.TrackingNumber, second.TrackingNumber]));

        Assert.Equal(1, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.Contains("modified by someone else", result.Results[0].Error, StringComparison.Ordinal);
    }
}
