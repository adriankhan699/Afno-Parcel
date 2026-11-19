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
    private readonly NetworkSetup _net = new();
    private readonly IParcelRepository _parcels = Substitute.For<IParcelRepository>();
    private readonly ParcelStatusService _sut;

    public ParcelStatusServiceTests()
    {
        // Staff work at the Kathmandu branch (and so at all of its warehouses) unless a test says otherwise.
        _net.AssignMe(_net.Kathmandu);

        _sut = new ParcelStatusService(
            _parcels,
            _net.Changer,
            _net.UnitOfWork,
            _net.CurrentUser,
            new UpdateParcelStatusRequestValidator(),
            NullLogger<ParcelStatusService>.Instance);
    }

    private IAuditLogRepository _audit => _net.Audit;

    private IUnitOfWork _unitOfWork => _net.UnitOfWork;

    private ICurrentUser _currentUser => _net.CurrentUser;

    private FixedClock _clock => _net.Clock;

    private Guid _userId => _net.UserId;

    private Branch _branch => _net.Kathmandu;

    private Warehouse _warehouse => _net.Hub;

    private void SignInAs(string role) => _net.SignInAs(role);

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
        SignInAs(Roles.BranchManager);
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

    // ---- changes made at a warehouse -----------------------------------------------------------------------------

    [Fact]
    public async Task Update_AtAWarehouse_AlsoRecordsTheWarehouseMovement()
    {
        SignInAs(Roles.WarehouseStaff);
        var parcel = Stored(ParcelStatusCode.InTransit);

        await _sut.UpdateAsync(parcel.Id, new UpdateParcelStatusRequest("ArrivedAtWarehouse", "At dock", WarehouseId: _warehouse.Id));

        _net.Movements.Received(1).AddMovement(Arg.Is<ParcelMovement>(m =>
            m.ParcelId == parcel.Id &&
            m.WarehouseId == _warehouse.Id &&
            m.ScanType == ScanType.Arrival &&
            m.StaffUserId == _userId &&
            m.Remarks == "At dock" &&
            !m.IsUnexpectedArrival));
    }

    [Fact]
    public async Task Update_AChangeAtABranch_RecordsNoWarehouseMovement()
    {
        SignInAs(Roles.BookingStaff);
        var parcel = Stored(ParcelStatusCode.PickedUp);

        await _sut.UpdateAsync(parcel.Id, new UpdateParcelStatusRequest("Shipped", BranchId: _branch.Id));

        _net.Movements.DidNotReceive().AddMovement(Arg.Any<ParcelMovement>());
    }

    [Fact]
    public async Task Update_DispatchingFromAWarehouse_CanNameTheNextWarehouse()
    {
        SignInAs(Roles.WarehouseStaff);
        var parcel = Stored(ParcelStatusCode.ArrivedAtWarehouse);

        var response = await _sut.UpdateAsync(
            parcel.Id,
            new UpdateParcelStatusRequest("InTransit", WarehouseId: _warehouse.Id, ToWarehouseId: _net.Hub2.Id));

        Assert.Equal("InTransit", response.Status);
        Assert.Equal(_net.Hub2.Id, parcel.NextWarehouseId);
        _net.Movements.Received(1).AddMovement(Arg.Is<ParcelMovement>(m => m.ScanType == ScanType.Dispatch && m.ToWarehouseId == _net.Hub2.Id));
    }

    [Fact]
    public async Task Update_ADestinationOnAnyOtherChange_OrToTheSameWarehouse_IsRefused()
    {
        SignInAs(Roles.WarehouseStaff);
        var arriving = Stored(ParcelStatusCode.InTransit);
        var leaving = Stored(ParcelStatusCode.ArrivedAtWarehouse);

        await Assert.ThrowsAsync<DomainException>(() =>
            _sut.UpdateAsync(arriving.Id, new UpdateParcelStatusRequest("ArrivedAtWarehouse", WarehouseId: _warehouse.Id, ToWarehouseId: _net.Hub2.Id)));
        await Assert.ThrowsAsync<DomainException>(() =>
            _sut.UpdateAsync(leaving.Id, new UpdateParcelStatusRequest("InTransit", WarehouseId: _warehouse.Id, ToWarehouseId: _warehouse.Id)));
        await Assert.ThrowsAsync<DomainException>(() =>
            _sut.UpdateAsync(leaving.Id, new UpdateParcelStatusRequest("InTransit", ToWarehouseId: _net.Hub2.Id)));

        Assert.Equal(ParcelStatusCode.InTransit, arriving.Status);
        Assert.Equal(ParcelStatusCode.ArrivedAtWarehouse, leaving.Status);
    }

    [Fact]
    public async Task Update_AtAWarehouse_ByStaffWhoAreNotAssignedThere_IsForbidden()
    {
        SignInAs(Roles.WarehouseStaff);
        var parcel = Stored(ParcelStatusCode.InTransit);

        _net.AssignMe(_net.Pokhara);
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            _sut.UpdateAsync(parcel.Id, new UpdateParcelStatusRequest("ArrivedAtWarehouse", WarehouseId: _warehouse.Id)));

        _net.AssignMe(_net.Kathmandu, _net.Hub2);
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            _sut.UpdateAsync(parcel.Id, new UpdateParcelStatusRequest("ArrivedAtWarehouse", WarehouseId: _warehouse.Id)));

        _net.Assignments.GetActiveByUserIdAsync(_userId, Arg.Any<CancellationToken>()).Returns((StaffAssignment?)null);
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            _sut.UpdateAsync(parcel.Id, new UpdateParcelStatusRequest("ArrivedAtWarehouse", WarehouseId: _warehouse.Id)));

        Assert.Equal(ParcelStatusCode.InTransit, parcel.Status);
        _net.Movements.DidNotReceive().AddMovement(Arg.Any<ParcelMovement>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_AtAWarehouse_ASuperAdministratorNeedsNoAssignment()
    {
        SignInAs(Roles.SuperAdmin);
        _net.Assignments.GetActiveByUserIdAsync(_userId, Arg.Any<CancellationToken>()).Returns((StaffAssignment?)null);
        var parcel = Stored(ParcelStatusCode.InTransit);

        var response = await _sut.UpdateAsync(parcel.Id, new UpdateParcelStatusRequest("ArrivedAtWarehouse", WarehouseId: _net.PokharaHub.Id));

        Assert.Equal("ArrivedAtWarehouse", response.Status);
    }

    [Fact]
    public async Task Update_RecordsWhenTheStatusLastChanged()
    {
        SignInAs(Roles.BookingStaff);
        var parcel = Stored();

        await _sut.UpdateAsync(parcel.Id, new UpdateParcelStatusRequest("PickedUp"));

        Assert.Equal(_clock.GetUtcNow(), parcel.StatusChangedAtUtc);
    }
}
