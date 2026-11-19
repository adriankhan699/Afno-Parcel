using Microsoft.Extensions.Logging.Abstractions;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Network;
using NepalParcel.Application.Tracking;
using NepalParcel.Contracts.Warehouses;
using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Warehouses;
using NSubstitute;

namespace NepalParcel.UnitTests.Application;

public sealed class WarehouseServiceTests
{
    private readonly NetworkSetup _net = new();
    private readonly WarehouseService _sut;

    public WarehouseServiceTests()
    {
        _net.SignInAs(Roles.SuperAdmin);
        _sut = new WarehouseService(
            _net.Warehouses,
            _net.Branches,
            _net.Inventory,
            _net.Access,
            _net.Audit,
            _net.UnitOfWork,
            _net.CurrentUser,
            _net.Client,
            _net.Clock,
            new CreateWarehouseRequestValidator(),
            new UpdateWarehouseRequestValidator(),
            new WarehouseQueryValidator(),
            NullLogger<WarehouseService>.Instance);
    }

    private CreateWarehouseRequest ValidCreate(string code = "ktmw3", Guid? branchId = null) =>
        new(branchId ?? _net.Kathmandu.Id, code, "Kathmandu West Hub", TestData.KathmanduAddressDto(), "9841000003", 500);

    private static UpdateWarehouseRequest ValidUpdate(int? capacity = 800) =>
        new("Renamed Hub", TestData.PokharaAddressDto(), "9841000009", capacity);

    // ---- create --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_ValidRequest_AddsSavesAndAudits()
    {
        var response = await _sut.CreateAsync(ValidCreate());

        Assert.Equal("KTMW3", response.Code);
        Assert.Equal("Kathmandu Main", response.BranchName);
        Assert.Equal(500, response.CapacityParcels);
        Assert.True(response.IsActive);
        _net.Warehouses.Received(1).Add(Arg.Is<Warehouse>(w => w.Code == "KTMW3" && w.BranchId == _net.Kathmandu.Id));
        _net.Audit.Received(1).Add(Arg.Is<AuditLog>(a => a.Action == AuditActions.WarehouseCreated));
        await _net.UnitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_DuplicateCode_IsAConflict()
    {
        _net.Warehouses.CodeExistsAsync("KTMW3", Arg.Any<CancellationToken>()).Returns(true);

        await Assert.ThrowsAsync<ConflictException>(() => _sut.CreateAsync(ValidCreate()));

        await _net.UnitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_UnknownOrInactiveBranch_IsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.CreateAsync(ValidCreate(branchId: Guid.NewGuid())));

        _net.Pokhara.Deactivate();
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.CreateAsync(ValidCreate(branchId: _net.Pokhara.Id)));
    }

    [Fact]
    public async Task CreateAsync_ABranchManagerMayOnlyAddToTheirOwnBranch()
    {
        _net.SignInAs(Roles.BranchManager);
        _net.AssignMe(_net.Kathmandu);

        var own = await _sut.CreateAsync(ValidCreate());
        await Assert.ThrowsAsync<ForbiddenException>(() => _sut.CreateAsync(ValidCreate("pkrw9", _net.Pokhara.Id)));

        Assert.Equal(_net.Kathmandu.Id, own.BranchId);
    }

    [Fact]
    public async Task CreateAsync_InvalidRequest_FailsValidation()
    {
        var ex = await Assert.ThrowsAsync<RequestValidationException>(() => _sut.CreateAsync(ValidCreate() with { PhoneNumber = "123" }));

        Assert.Contains("PhoneNumber", ex.Errors.Keys);
    }

    // ---- read ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByIdAsync_ReturnsTheCountsOfParcelsHeldAndInbound()
    {
        _net.Inventory.CountHeldByWarehouseAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [_net.Hub.Id] = 12 });
        _net.Inventory.CountInboundByWarehouseAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [_net.Hub.Id] = 3 });
        _net.Branches.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, NepalParcel.Domain.Branches.Branch> { [_net.Kathmandu.Id] = _net.Kathmandu });

        var response = await _sut.GetByIdAsync(_net.Hub.Id);

        Assert.Equal(12, response.ParcelsHeld);
        Assert.Equal(3, response.ParcelsInbound);
        Assert.Equal("Kathmandu Main", response.BranchName);
    }

    [Fact]
    public async Task GetByIdAsync_Missing_IsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.GetByIdAsync(Guid.NewGuid()));
    }

    [Theory]
    [InlineData(Roles.SuperAdmin, true)]
    [InlineData(Roles.BranchManager, true)]
    [InlineData(Roles.WarehouseStaff, false)]
    public async Task GetPageAsync_OnlyNetworkAdminsMayIncludeInactiveWarehouses(string role, bool allowed)
    {
        _net.SignInAs(role);
        _net.Warehouses.SearchAsync(Arg.Any<WarehouseFilter>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(([], 0));

        await _sut.GetPageAsync(new WarehouseQuery { IncludeInactive = true, BranchId = _net.Kathmandu.Id, Search = " hub " });

        await _net.Warehouses.Received(1).SearchAsync(
            Arg.Is<WarehouseFilter>(f => f.IncludeInactive == allowed && f.BranchId == _net.Kathmandu.Id && f.Search == "hub"),
            1,
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // ---- update --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task UpdateAsync_ChangesTheDetailsButNotTheCodeOrBranch()
    {
        var response = await _sut.UpdateAsync(_net.Hub.Id, ValidUpdate());

        Assert.Equal("Renamed Hub", response.Name);
        Assert.Equal("KTMW1", response.Code);
        Assert.Equal(_net.Kathmandu.Id, response.BranchId);
        Assert.Equal(800, response.CapacityParcels);
        _net.Audit.Received(1).Add(Arg.Is<AuditLog>(a => a.Action == AuditActions.WarehouseUpdated));
        await _net.UnitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_ZeroCapacity_FailsValidation()
    {
        var ex = await Assert.ThrowsAsync<RequestValidationException>(() => _sut.UpdateAsync(_net.Hub.Id, ValidUpdate(capacity: 0)));

        Assert.Contains("CapacityParcels", ex.Errors.Keys);
    }

    [Fact]
    public async Task UpdateAsync_ABranchManagerMayNotTouchAnotherBranchsWarehouse()
    {
        _net.SignInAs(Roles.BranchManager);
        _net.AssignMe(_net.Kathmandu);

        await _sut.UpdateAsync(_net.Hub.Id, ValidUpdate());
        await Assert.ThrowsAsync<ForbiddenException>(() => _sut.UpdateAsync(_net.PokharaHub.Id, ValidUpdate()));
    }

    // ---- activate / deactivate -----------------------------------------------------------------------------------

    [Fact]
    public async Task DeactivateAsync_AnEmptyWarehouse_IsSwitchedOff()
    {
        var response = await _sut.DeactivateAsync(_net.Hub.Id);

        Assert.False(response.IsActive);
        _net.Audit.Received(1).Add(Arg.Is<AuditLog>(a => a.Action == AuditActions.WarehouseDeactivated));
    }

    [Fact]
    public async Task DeactivateAsync_WhileItHoldsParcels_IsRefused()
    {
        _net.Inventory.CountHeldByWarehouseAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [_net.Hub.Id] = 9 });

        var ex = await Assert.ThrowsAsync<DomainException>(() => _sut.DeactivateAsync(_net.Hub.Id));

        Assert.Contains("9 parcel", ex.Message, StringComparison.Ordinal);
        Assert.True(_net.Hub.IsActive);
    }

    [Fact]
    public async Task DeactivateAsync_WhileParcelsAreOnTheirWayToIt_IsRefused()
    {
        _net.Inventory.CountInboundByWarehouseAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [_net.Hub.Id] = 2 });

        var ex = await Assert.ThrowsAsync<DomainException>(() => _sut.DeactivateAsync(_net.Hub.Id));

        Assert.Contains("on their way", ex.Message, StringComparison.Ordinal);
        Assert.True(_net.Hub.IsActive);
    }

    [Fact]
    public async Task ActivateAsync_NeedsAnActiveBranch_AndAnInactiveWarehouse()
    {
        _net.Hub.Deactivate();
        _net.Kathmandu.Deactivate();

        var ex = await Assert.ThrowsAsync<DomainException>(() => _sut.ActivateAsync(_net.Hub.Id));
        Assert.Contains("branch is not active", ex.Message, StringComparison.Ordinal);

        _net.Kathmandu.Activate();
        var response = await _sut.ActivateAsync(_net.Hub.Id);
        Assert.True(response.IsActive);
        await Assert.ThrowsAsync<ConflictException>(() => _sut.ActivateAsync(_net.Hub.Id));
    }

    [Fact]
    public async Task ActivateAndDeactivate_AreForbiddenToAnUnassignedBranchManager()
    {
        _net.SignInAs(Roles.BranchManager);

        await Assert.ThrowsAsync<ForbiddenException>(() => _sut.DeactivateAsync(_net.Hub.Id));
        await Assert.ThrowsAsync<ForbiddenException>(() => _sut.ActivateAsync(_net.Hub.Id));
    }
}
