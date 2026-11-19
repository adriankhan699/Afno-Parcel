using Microsoft.Extensions.Logging.Abstractions;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Network;
using NepalParcel.Application.Parcels;
using NepalParcel.Application.Tracking;
using NepalParcel.Contracts.Tracking;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Warehouses;
using NepalParcel.UnitTests.Domain;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace NepalParcel.UnitTests.Application;

public sealed class ScanServiceTests
{
    private readonly NetworkSetup _net = new();
    private readonly IParcelRepository _parcels = Substitute.For<IParcelRepository>();
    private readonly Dictionary<Guid, ParcelMovement> _latestMovement = [];
    private readonly List<ParcelMovement> _movements = [];
    private readonly List<ParcelScan> _scans = [];
    private readonly ScanService _sut;

    public ScanServiceTests()
    {
        // A tiny in-memory stand-in for the movement table: remembers what was added and answers "what was the last movement?".
        _net.Movements
            .When(m => m.AddMovement(Arg.Any<ParcelMovement>()))
            .Do(call =>
            {
                var movement = call.Arg<ParcelMovement>();
                _movements.Add(movement);
                _latestMovement[movement.ParcelId] = movement;
            });
        _net.Movements.When(m => m.AddScan(Arg.Any<ParcelScan>())).Do(call => _scans.Add(call.Arg<ParcelScan>()));
        _net.Movements
            .GetLatestMovementAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(_latestMovement.GetValueOrDefault(call.Arg<Guid>())));

        _net.SignInAs(Roles.WarehouseStaff);
        _net.AssignMe(_net.Kathmandu);

        _sut = new ScanService(
            _parcels,
            _net.Movements,
            _net.Warehouses,
            _net.Changer,
            _net.Access,
            _net.UnitOfWork,
            _net.CurrentUser,
            _net.Clock,
            new ScanRequestValidator(),
            new BatchScanRequestValidator(),
            new ScanLogQueryValidator(),
            NullLogger<ScanService>.Instance);
    }

    private Parcel Stored(ParcelStatusCode at)
    {
        var parcel = ParcelTests.ParcelAt(at);
        _parcels.GetByIdAsync(parcel.Id, Arg.Any<CancellationToken>()).Returns(parcel);
        _parcels.GetByTrackingNumberAsync(parcel.TrackingNumber, Arg.Any<CancellationToken>()).Returns(parcel);
        return parcel;
    }

    // ---- recording a scan ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Arrival_RecordsTheMovementAndTheScan_WithEveryRequiredField()
    {
        var parcel = Stored(ParcelStatusCode.InTransit);

        var result = await _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Arrival", _net.Hub.Id, "Scanned at dock 2"));

        Assert.Equal("Applied", result.Outcome);
        Assert.True(result.Succeeded);
        Assert.Equal("ArrivedAtWarehouse", result.Status);
        Assert.Equal("Kathmandu Hub", result.Warehouse);
        Assert.Equal(ParcelStatusCode.ArrivedAtWarehouse, parcel.Status);

        var movement = Assert.Single(_movements);
        Assert.Equal(parcel.Id, movement.ParcelId);
        Assert.Equal(_net.Hub.Id, movement.WarehouseId);
        Assert.Equal(_net.UserId, movement.StaffUserId);
        Assert.Equal(ScanType.Arrival, movement.ScanType);
        Assert.Equal(_net.Clock.GetUtcNow(), movement.OccurredAtUtc);
        Assert.Equal("Scanned at dock 2", movement.Remarks);

        var scan = Assert.Single(_scans);
        Assert.Equal(parcel.Id, scan.ParcelId);
        Assert.Equal(_net.Hub.Id, scan.WarehouseId);
        Assert.Equal(_net.UserId, scan.StaffUserId);
        Assert.Equal("Hari Staff", scan.StaffName);
        Assert.Equal(ScanType.Arrival, scan.ScanType);
        Assert.Equal(_net.Clock.GetUtcNow(), scan.ScannedAtUtc);
        Assert.Equal("Scanned at dock 2", scan.Remarks);
        Assert.Equal(ScanOutcome.Applied, scan.Outcome);
        Assert.Equal(movement.Id, scan.MovementId);

        var historyRow = parcel.StatusHistory.OrderBy(h => h.OccurredAtUtc).Last();
        Assert.Equal(_net.Hub.Id, historyRow.WarehouseId);
        await _net.UnitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Dispatch", ParcelStatusCode.ArrivedAtWarehouse, "InTransit")]
    [InlineData("OutForDelivery", ParcelStatusCode.ArrivedAtWarehouse, "OutForDelivery")]
    [InlineData("arrival", ParcelStatusCode.InTransit, "ArrivedAtWarehouse")]
    public async Task ScanTypes_MapToTheRightStatus(string scanType, ParcelStatusCode startAt, string expected)
    {
        var parcel = Stored(startAt);

        var result = await _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, scanType, _net.Hub.Id));

        Assert.Equal(expected, result.Status);
        Assert.Single(_movements);
    }

    [Fact]
    public async Task AQrCodesTrackingLink_IsUnderstoodAsWellAsTheBareNumber()
    {
        var parcel = Stored(ParcelStatusCode.InTransit);

        var result = await _sut.ScanAsync(new ScanRequest($"https://afno.example/track/{parcel.TrackingNumber.ToLowerInvariant()}?x=1", "Arrival", _net.Hub.Id));

        Assert.Equal(parcel.TrackingNumber, result.TrackingNumber);
        Assert.Equal("Applied", result.Outcome);
    }

    [Fact]
    public async Task ADispatchWithADestination_RemembersWhereTheParcelIsHeaded()
    {
        var parcel = Stored(ParcelStatusCode.ArrivedAtWarehouse);

        var result = await _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Dispatch", _net.Hub.Id, ToWarehouseId: _net.Hub2.Id));

        Assert.Equal("Kathmandu East Hub", result.ToWarehouse);
        Assert.Equal(_net.Hub2.Id, parcel.NextWarehouseId);
        Assert.Equal(_net.Hub2.Id, Assert.Single(_movements).ToWarehouseId);
    }

    [Fact]
    public async Task ADispatchToTheSameWarehouse_IsRefused()
    {
        var parcel = Stored(ParcelStatusCode.ArrivedAtWarehouse);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Dispatch", _net.Hub.Id, ToWarehouseId: _net.Hub.Id)));

        Assert.Contains("warehouse it is leaving", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_movements);
        Assert.Equal(ParcelStatusCode.ArrivedAtWarehouse, parcel.Status);
    }

    [Fact]
    public async Task ADestinationOnAnythingButADispatch_IsRefused()
    {
        var parcel = Stored(ParcelStatusCode.InTransit);

        await Assert.ThrowsAsync<DomainException>(() =>
            _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Arrival", _net.Hub.Id, ToWarehouseId: _net.Hub2.Id)));

        Assert.Empty(_movements);
    }

    [Fact]
    public async Task ADestinationThatDoesNotExistOrIsSwitchedOff_IsRefused()
    {
        var parcel = Stored(ParcelStatusCode.ArrivedAtWarehouse);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Dispatch", _net.Hub.Id, ToWarehouseId: Guid.NewGuid())));

        _net.Hub2.Deactivate();
        await Assert.ThrowsAsync<DomainException>(() =>
            _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Dispatch", _net.Hub.Id, ToWarehouseId: _net.Hub2.Id)));
        Assert.Equal(ParcelStatusCode.ArrivedAtWarehouse, parcel.Status);
    }

    // ---- duplicate scans -----------------------------------------------------------------------------------------

    [Fact]
    public async Task ScanningTheSameThingTwice_LogsTheRepeat_ButCreatesNoSecondMovement()
    {
        var parcel = Stored(ParcelStatusCode.InTransit);
        var first = await _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Arrival", _net.Hub.Id));
        var historyAfterFirst = parcel.StatusHistory.Count;

        var second = await _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Arrival", _net.Hub.Id, "Scanned again"));

        Assert.Equal("Applied", first.Outcome);
        Assert.Equal("Duplicate", second.Outcome);
        Assert.True(second.Succeeded);
        Assert.Contains("Already recorded", second.Message, StringComparison.Ordinal);
        Assert.Single(_movements);
        Assert.Equal(historyAfterFirst, parcel.StatusHistory.Count);
        Assert.Equal(ParcelStatusCode.ArrivedAtWarehouse, parcel.Status);

        Assert.Equal(2, _scans.Count);
        Assert.Equal(ScanOutcome.Duplicate, _scans[1].Outcome);
        Assert.Equal(_movements[0].Id, _scans[1].MovementId);
        Assert.Equal("Scanned again", _scans[1].Remarks);
    }

    [Fact]
    public async Task ARepeatedDispatch_IsAlsoADuplicate()
    {
        var parcel = Stored(ParcelStatusCode.ArrivedAtWarehouse);
        await _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Dispatch", _net.Hub.Id, ToWarehouseId: _net.Hub2.Id));

        var again = await _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Dispatch", _net.Hub.Id, ToWarehouseId: _net.Hub2.Id));

        Assert.Equal("Duplicate", again.Outcome);
        Assert.Single(_movements);
        Assert.Equal(ParcelStatusCode.InTransit, parcel.Status);
    }

    [Fact]
    public async Task ARepeatedScan_ByAPersonWhoCannotWorkThere_IsStillRefused()
    {
        var parcel = Stored(ParcelStatusCode.InTransit);
        await _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Arrival", _net.Hub.Id));
        _net.AssignMe(_net.Pokhara);

        await Assert.ThrowsAsync<ForbiddenException>(() => _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Arrival", _net.Hub.Id)));

        Assert.Single(_scans);
    }

    [Fact]
    public async Task AFullJourney_ArriveDispatchArriveAgain_IsNotMistakenForDuplicates()
    {
        var parcel = Stored(ParcelStatusCode.InTransit);
        _net.SignInAs(Roles.SuperAdmin);

        var a = await _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Arrival", _net.Hub.Id));
        var b = await _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Dispatch", _net.Hub.Id, ToWarehouseId: _net.Hub2.Id));
        var c = await _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Arrival", _net.Hub2.Id));

        Assert.All([a, b, c], r => Assert.Equal("Applied", r.Outcome));
        Assert.Equal(3, _movements.Count);
        Assert.False(_movements[2].IsUnexpectedArrival);
        Assert.Null(parcel.NextWarehouseId);
        Assert.Equal(ParcelStatusCode.ArrivedAtWarehouse, parcel.Status);
    }

    [Fact]
    public async Task AnArrivalAtTheWrongWarehouse_IsRecorded_ButFlaggedAsUnexpected()
    {
        var parcel = Stored(ParcelStatusCode.ArrivedAtWarehouse);
        _net.SignInAs(Roles.SuperAdmin);
        await _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Dispatch", _net.Hub.Id, ToWarehouseId: _net.PokharaHub.Id));

        var arrival = await _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Arrival", _net.Hub2.Id));

        Assert.Equal("Applied", arrival.Outcome);
        Assert.Contains("different warehouse", arrival.Message, StringComparison.Ordinal);
        Assert.True(_movements[^1].IsUnexpectedArrival);
        Assert.Equal(ParcelStatusCode.ArrivedAtWarehouse, parcel.Status);
    }

    // ---- who may scan, and what ----------------------------------------------------------------------------------

    [Fact]
    public async Task ScanningAParcelThatIsInTheWrongState_IsRefusedWithAnExplanation()
    {
        var parcel = Stored(ParcelStatusCode.PickedUp);

        var ex = await Assert.ThrowsAsync<DomainException>(() => _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Arrival", _net.Hub.Id)));

        Assert.Contains("cannot move to", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_movements);
        Assert.Empty(_scans);
        Assert.Equal(ParcelStatusCode.PickedUp, parcel.Status);
    }

    [Theory]
    [InlineData(Roles.BookingStaff)]
    [InlineData(Roles.Accountant)]
    [InlineData(Roles.Customer)]
    public async Task ScanningByARoleThatMayNotScan_IsForbidden(string role)
    {
        _net.SignInAs(role);
        var parcel = Stored(ParcelStatusCode.InTransit);

        await Assert.ThrowsAsync<ForbiddenException>(() => _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Arrival", _net.Hub.Id)));

        Assert.Empty(_movements);
    }

    [Fact]
    public async Task Anonymous_IsUnauthorized()
    {
        _net.CurrentUser.UserId.Returns((Guid?)null);
        var parcel = Stored(ParcelStatusCode.InTransit);

        await Assert.ThrowsAsync<UnauthorizedException>(() => _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Arrival", _net.Hub.Id)));
    }

    [Fact]
    public async Task StaffWhoAreNotAssignedToAnyBranch_CannotScanAtAWarehouse()
    {
        _net.Assignments.GetActiveByUserIdAsync(_net.UserId, Arg.Any<CancellationToken>()).Returns((StaffAssignment?)null);
        var parcel = Stored(ParcelStatusCode.InTransit);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Arrival", _net.Hub.Id)));

        Assert.Contains("not assigned", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_movements);
    }

    [Fact]
    public async Task StaffOfAnotherBranch_CannotScanAtThisWarehouse()
    {
        _net.AssignMe(_net.Pokhara);
        var parcel = Stored(ParcelStatusCode.InTransit);

        await Assert.ThrowsAsync<ForbiddenException>(() => _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Arrival", _net.Hub.Id)));
    }

    [Fact]
    public async Task StaffAssignedToOneWarehouse_CannotScanAtItsSibling_ButCanAtTheirOwn()
    {
        _net.AssignMe(_net.Kathmandu, _net.Hub);
        var parcel = Stored(ParcelStatusCode.InTransit);

        await Assert.ThrowsAsync<ForbiddenException>(() => _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Arrival", _net.Hub2.Id)));
        var ok = await _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Arrival", _net.Hub.Id));

        Assert.Equal("Applied", ok.Outcome);
    }

    [Fact]
    public async Task ASuperAdministrator_CanScanAnywhere_WithoutAnAssignment()
    {
        _net.SignInAs(Roles.SuperAdmin);
        _net.Assignments.GetActiveByUserIdAsync(_net.UserId, Arg.Any<CancellationToken>()).Returns((StaffAssignment?)null);
        var parcel = Stored(ParcelStatusCode.InTransit);

        var result = await _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Arrival", _net.PokharaHub.Id));

        Assert.Equal("Applied", result.Outcome);
    }

    [Fact]
    public async Task UnknownTrackingNumber_UnknownWarehouse_SwitchedOffWarehouse_AndBadType_AreRefused()
    {
        var parcel = Stored(ParcelStatusCode.InTransit);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.ScanAsync(new ScanRequest(TrackingNumbers.Generate(), "Arrival", _net.Hub.Id)));
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.ScanAsync(new ScanRequest("nonsense", "Arrival", _net.Hub.Id)));
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Arrival", Guid.NewGuid())));
        await Assert.ThrowsAsync<RequestValidationException>(() => _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Teleport", _net.Hub.Id)));
        await Assert.ThrowsAsync<RequestValidationException>(() => _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Arrival", Guid.Empty)));

        _net.Hub.Deactivate();
        await Assert.ThrowsAsync<DomainException>(() => _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Arrival", _net.Hub.Id)));
        Assert.Equal(ParcelStatusCode.InTransit, parcel.Status);
        Assert.Empty(_movements);
    }

    [Fact]
    public async Task WhenSomeoneElseChangedTheParcelFirst_TheConflictIsSurfaced()
    {
        var parcel = Stored(ParcelStatusCode.InTransit);
        _net.UnitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new ConcurrencyException());

        await Assert.ThrowsAsync<ConcurrencyException>(() => _sut.ScanAsync(new ScanRequest(parcel.TrackingNumber, "Arrival", _net.Hub.Id)));
    }

    // ---- batch scanning ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Batch_EachParcelSucceedsOrFailsOnItsOwn()
    {
        var good1 = Stored(ParcelStatusCode.InTransit);
        var good2 = Stored(ParcelStatusCode.Shipped);
        var wrongState = Stored(ParcelStatusCode.PickedUp);
        var unknown = TrackingNumbers.Generate();

        var result = await _sut.ScanBatchAsync(new BatchScanRequest(
            "Arrival",
            _net.Hub.Id,
            [good1.TrackingNumber, wrongState.TrackingNumber, unknown, "not-a-number", good2.TrackingNumber]));

        Assert.Equal(2, result.Succeeded);
        Assert.Equal(0, result.Duplicates);
        Assert.Equal(3, result.Failed);
        Assert.Equal(5, result.Results.Count);
        Assert.Equal("Applied", result.Results.Single(r => r.TrackingNumber == good1.TrackingNumber).Outcome);
        Assert.Equal("Applied", result.Results.Single(r => r.TrackingNumber == good2.TrackingNumber).Outcome);
        Assert.Contains("cannot move to", result.Results.Single(r => r.TrackingNumber == wrongState.TrackingNumber).Error, StringComparison.Ordinal);
        Assert.Contains("was not found", result.Results.Single(r => r.TrackingNumber == unknown).Error, StringComparison.Ordinal);
        Assert.False(result.Results.Single(r => r.TrackingNumber == "NOTANUMBER").Succeeded);

        Assert.Equal(ParcelStatusCode.ArrivedAtWarehouse, good1.Status);
        Assert.Equal(ParcelStatusCode.ArrivedAtWarehouse, good2.Status);
        Assert.Equal(ParcelStatusCode.PickedUp, wrongState.Status);
        Assert.Equal(2, _movements.Count);
        _net.UnitOfWork.Received(3).DiscardPendingChanges(); // each refused parcel drops whatever was staged for it
    }

    [Fact]
    public async Task Batch_RepeatingTheSameBatch_ReportsEveryParcelAsADuplicate()
    {
        var one = Stored(ParcelStatusCode.InTransit);
        var two = Stored(ParcelStatusCode.InTransit);
        var request = new BatchScanRequest("Arrival", _net.Hub.Id, [one.TrackingNumber, two.TrackingNumber]);
        await _sut.ScanBatchAsync(request);

        var again = await _sut.ScanBatchAsync(request);

        Assert.Equal(0, again.Succeeded);
        Assert.Equal(2, again.Duplicates);
        Assert.Equal(0, again.Failed);
        Assert.Equal(2, _movements.Count);
        Assert.Equal(2, _scans.Count(s => s.Outcome == ScanOutcome.Duplicate));
    }

    [Fact]
    public async Task Batch_ScansEachParcelOnce_EvenIfItIsListedTwiceOrAsALink()
    {
        var parcel = Stored(ParcelStatusCode.InTransit);

        var result = await _sut.ScanBatchAsync(new BatchScanRequest(
            "Arrival",
            _net.Hub.Id,
            [parcel.TrackingNumber, parcel.TrackingNumber.ToLowerInvariant(), $"https://afno.example/track/{parcel.TrackingNumber}"]));

        Assert.Single(result.Results);
        Assert.Equal(1, result.Succeeded);
        Assert.Single(_movements);
    }

    [Fact]
    public async Task Batch_ABlankEntry_FailsValidation_BeforeAnythingIsScanned()
    {
        var parcel = Stored(ParcelStatusCode.InTransit);

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            _sut.ScanBatchAsync(new BatchScanRequest("Arrival", _net.Hub.Id, [parcel.TrackingNumber, "  "])));

        Assert.Equal(ParcelStatusCode.InTransit, parcel.Status);
    }

    [Fact]
    public async Task Batch_ABadWarehouse_OrAnUnauthorisedPerson_FailsTheWholeRequest()
    {
        var parcel = Stored(ParcelStatusCode.InTransit);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.ScanBatchAsync(new BatchScanRequest("Arrival", Guid.NewGuid(), [parcel.TrackingNumber])));

        _net.AssignMe(_net.Pokhara);
        await Assert.ThrowsAsync<ForbiddenException>(() => _sut.ScanBatchAsync(new BatchScanRequest("Arrival", _net.Hub.Id, [parcel.TrackingNumber])));

        Assert.Equal(ParcelStatusCode.InTransit, parcel.Status);
    }

    [Fact]
    public async Task Batch_ByARoleThatMayNotScan_IsForbidden()
    {
        _net.SignInAs(Roles.DeliveryAgent);
        var parcel = Stored(ParcelStatusCode.InTransit);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            _sut.ScanBatchAsync(new BatchScanRequest("Arrival", _net.Hub.Id, [parcel.TrackingNumber])));
    }

    [Fact]
    public async Task Batch_Limits_AreEnforced()
    {
        await Assert.ThrowsAsync<RequestValidationException>(() => _sut.ScanBatchAsync(new BatchScanRequest("Arrival", _net.Hub.Id, [])));
        var tooMany = Enumerable.Range(0, 101).Select(_ => TrackingNumbers.Generate()).ToList();
        await Assert.ThrowsAsync<RequestValidationException>(() => _sut.ScanBatchAsync(new BatchScanRequest("Arrival", _net.Hub.Id, tooMany)));

        var max = Enumerable.Range(0, 100).Select(_ => TrackingNumbers.Generate()).ToList();
        var result = await _sut.ScanBatchAsync(new BatchScanRequest("Arrival", _net.Hub.Id, max));
        Assert.Equal(100, result.Failed);
    }

    [Fact]
    public async Task Batch_AConflictOnOneParcel_DoesNotStopTheRest()
    {
        var first = Stored(ParcelStatusCode.InTransit);
        var second = Stored(ParcelStatusCode.InTransit);
        _net.UnitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(
            _ => throw new ConcurrencyException(),
            _ => Task.FromResult(1));

        var result = await _sut.ScanBatchAsync(new BatchScanRequest("Arrival", _net.Hub.Id, [first.TrackingNumber, second.TrackingNumber]));

        Assert.Equal(1, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.Contains("modified by someone else", result.Results[0].Error, StringComparison.Ordinal);
    }

    // ---- the scan log and the movement history -------------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_TurnsTheQueryIntoAFilter_UsingNepalCalendarDays()
    {
        var parcel = Stored(ParcelStatusCode.InTransit);
        var staff = Guid.NewGuid();
        _net.Movements
            .SearchScansAsync(Arg.Any<ScanLogFilter>(), 2, 10, Arg.Any<CancellationToken>())
            .Returns((new List<ScanLogItem>
            {
                new(Guid.NewGuid(), parcel.Id, parcel.TrackingNumber, _net.Hub.Id, "Kathmandu Hub", staff, "Hari", ScanType.Arrival, ScanOutcome.Duplicate, TestData.Now, "again", Guid.NewGuid()),
            }, 11));

        var page = await _sut.SearchAsync(new ScanLogQuery
        {
            Page = 2,
            PageSize = 10,
            WarehouseId = _net.Hub.Id,
            TrackingNumber = $"https://afno.example/track/{parcel.TrackingNumber}",
            ScanType = "arrival",
            Outcome = "duplicate",
            StaffUserId = staff,
            From = new DateOnly(2026, 11, 10),
            To = new DateOnly(2026, 11, 12),
        });

        var item = Assert.Single(page.Items);
        Assert.Equal("Duplicate", item.Outcome);
        Assert.Equal("Arrival", item.ScanType);
        Assert.Equal(11, page.TotalCount);
        var nepal = TimeSpan.FromMinutes(345);
        await _net.Movements.Received(1).SearchScansAsync(
            Arg.Is<ScanLogFilter>(f =>
                f.WarehouseId == _net.Hub.Id &&
                f.TrackingNumber == parcel.TrackingNumber &&
                f.ScanType == ScanType.Arrival &&
                f.Outcome == ScanOutcome.Duplicate &&
                f.StaffUserId == staff &&
                f.FromUtc == new DateTimeOffset(2026, 11, 10, 0, 0, 0, nepal).ToUniversalTime() &&
                f.BeforeUtc == new DateTimeOffset(2026, 11, 13, 0, 0, 0, nepal).ToUniversalTime()),
            2,
            10,
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Teleport", null)]
    [InlineData(null, "Maybe")]
    public async Task SearchAsync_UnknownScanTypeOrOutcome_FailsValidation(string? scanType, string? outcome)
    {
        await Assert.ThrowsAsync<RequestValidationException>(() => _sut.SearchAsync(new ScanLogQuery { ScanType = scanType, Outcome = outcome }));
    }

    [Fact]
    public async Task SearchAsync_AnEndDateBeforeTheStartDate_FailsValidation()
    {
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            _sut.SearchAsync(new ScanLogQuery { From = new DateOnly(2026, 11, 12), To = new DateOnly(2026, 11, 10) }));
    }

    [Fact]
    public async Task GetMovementsAsync_ReturnsTheHistoryOldestFirst_WithWarehouseNames()
    {
        var parcel = Stored(ParcelStatusCode.ArrivedAtWarehouse);
        var arrival = ParcelMovement.Create(parcel.Id, _net.Hub.Id, ScanType.Arrival, _net.UserId, "Hari", TestData.Now.AddHours(1), "ok");
        var dispatch = ParcelMovement.Create(parcel.Id, _net.Hub.Id, ScanType.Dispatch, _net.UserId, "Hari", TestData.Now.AddHours(3), toWarehouseId: _net.Hub2.Id);
        _net.Movements.GetMovementsAsync(parcel.Id, Arg.Any<CancellationToken>()).Returns(new List<ParcelMovement> { dispatch, arrival });
        _net.Warehouses.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Warehouse> { [_net.Hub.Id] = _net.Hub, [_net.Hub2.Id] = _net.Hub2 });

        var history = await _sut.GetMovementsAsync(parcel.Id);

        Assert.Equal(["Arrival", "Dispatch"], history.Select(h => h.ScanType));
        Assert.Equal("Kathmandu Hub", history[0].WarehouseName);
        Assert.Equal("Kathmandu East Hub", history[1].ToWarehouseName);
        Assert.Equal("ok", history[0].Remarks);
    }

    [Fact]
    public async Task GetMovementsAsync_UnknownParcel_IsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.GetMovementsAsync(Guid.NewGuid()));
    }
}
