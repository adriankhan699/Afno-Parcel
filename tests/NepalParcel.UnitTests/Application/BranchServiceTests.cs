using Microsoft.Extensions.Logging.Abstractions;
using NepalParcel.Application.Branches;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Network;
using NepalParcel.Contracts.Branches;
using NepalParcel.Contracts.Common;
using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Identity;
using NSubstitute;

namespace NepalParcel.UnitTests.Application;

public sealed class BranchServiceTests
{
    private readonly NetworkSetup _net = new();
    private readonly BranchService _sut;

    public BranchServiceTests()
    {
        _net.SignInAs(Roles.SuperAdmin);
        _sut = new BranchService(
            _net.Branches,
            _net.Warehouses,
            _net.Assignments,
            _net.Inventory,
            _net.Access,
            _net.Audit,
            _net.UnitOfWork,
            _net.CurrentUser,
            _net.Client,
            _net.Clock,
            new CreateBranchRequestValidator(),
            new UpdateBranchRequestValidator(),
            new BranchQueryValidator(),
            NullLogger<BranchService>.Instance);
    }

    private static UpdateBranchRequest ValidUpdate(string name = "Kathmandu Central") =>
        new(name, TestData.PokharaAddressDto(), "9841999999", "central@nepalparcel.test");

    // ---- create --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_ValidRequest_AddsSavesAndAudits()
    {
        var response = await _sut.CreateAsync(TestData.ValidCreateBranchRequest("ktm02"));

        Assert.Equal("KTM02", response.Code);
        Assert.Equal("Bagmati", response.Address.Province);
        _net.Branches.Received(1).Add(Arg.Is<Branch>(b => b.Code == "KTM02" && b.Type == BranchType.Branch));
        _net.Audit.Received(1).Add(Arg.Is<AuditLog>(a => a.Action == AuditActions.BranchCreated));
        await _net.UnitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_DuplicateCode_ThrowsConflict_AndDoesNotSave()
    {
        _net.Branches.CodeExistsAsync("KTM01", Arg.Any<CancellationToken>()).Returns(true);

        await Assert.ThrowsAsync<ConflictException>(() => _sut.CreateAsync(TestData.ValidCreateBranchRequest("ktm01")));

        _net.Branches.DidNotReceive().Add(Arg.Any<Branch>());
        await _net.UnitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_InvalidRequest_ThrowsValidationWithErrors()
    {
        var request = TestData.ValidCreateBranchRequest() with { PhoneNumber = "abc" };

        var ex = await Assert.ThrowsAsync<RequestValidationException>(() => _sut.CreateAsync(request));

        Assert.True(ex.Errors.ContainsKey("PhoneNumber"));
    }

    // ---- read ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByIdAsync_Missing_ThrowsNotFound()
    {
        _net.Branches.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Branch?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.GetByIdAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetByIdAsync_IncludesTheActiveWarehouseAndStaffCounts()
    {
        var id = _net.Kathmandu.Id;
        _net.Warehouses.CountByBranchAsync(Arg.Any<IReadOnlyCollection<Guid>>(), true, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [id] = 3 });
        _net.Assignments.CountActiveByBranchAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [id] = 7 });

        var response = await _sut.GetByIdAsync(id);

        Assert.Equal(3, response.WarehouseCount);
        Assert.Equal(7, response.StaffCount);
    }

    [Fact]
    public async Task GetPageAsync_MapsItemsAndPaging()
    {
        var branch = Branch.Create("BRT01", "Biratnagar", BranchType.Branch, TestData.KathmanduAddress(), "9852000000");
        _net.Branches.SearchAsync(Arg.Any<BranchFilter>(), 2, 10, Arg.Any<CancellationToken>()).Returns(([branch], 11));

        var page = await _sut.GetPageAsync(new BranchQuery { Page = 2, PageSize = 10 });

        Assert.Single(page.Items);
        Assert.Equal(11, page.TotalCount);
        Assert.Equal(2, page.TotalPages);
        Assert.False(page.HasNextPage);
    }

    [Fact]
    public async Task GetPageAsync_PageSizeTooLarge_ThrowsValidation()
    {
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            _sut.GetPageAsync(new BranchQuery { Page = 1, PageSize = PageQuery.MaxPageSize + 1 }));
    }

    [Fact]
    public async Task GetPageAsync_UnknownType_ThrowsValidation()
    {
        var ex = await Assert.ThrowsAsync<RequestValidationException>(() => _sut.GetPageAsync(new BranchQuery { Type = "Depot" }));

        Assert.Contains("Type", ex.Errors.Keys);
    }

    [Theory]
    [InlineData(Roles.SuperAdmin, true)]
    [InlineData(Roles.BranchManager, true)]
    [InlineData(Roles.Customer, false)]
    [InlineData(Roles.BookingStaff, false)]
    public async Task GetPageAsync_OnlyNetworkAdminsMayIncludeInactiveBranches(string role, bool allowed)
    {
        _net.SignInAs(role);
        _net.Branches.SearchAsync(Arg.Any<BranchFilter>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(([], 0));

        await _sut.GetPageAsync(new BranchQuery { IncludeInactive = true, Search = " ktm ", Type = "branch" });

        await _net.Branches.Received(1).SearchAsync(
            Arg.Is<BranchFilter>(f => f.IncludeInactive == allowed && f.Search == "ktm" && f.Type == BranchType.Branch),
            1,
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // ---- update --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task UpdateAsync_ChangesTheDetailsButNotTheCode_AndAudits()
    {
        var branch = _net.Kathmandu;

        var response = await _sut.UpdateAsync(branch.Id, ValidUpdate());

        Assert.Equal("Kathmandu Central", response.Name);
        Assert.Equal("KTM01", response.Code);
        Assert.Equal("9841999999", response.PhoneNumber);
        Assert.Equal("Gandaki", response.Address.Province);
        _net.Audit.Received(1).Add(Arg.Is<AuditLog>(a => a.Action == AuditActions.BranchUpdated));
        await _net.UnitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_InvalidRequest_ThrowsValidation_AndDoesNotSave()
    {
        var ex = await Assert.ThrowsAsync<RequestValidationException>(() =>
            _sut.UpdateAsync(_net.Kathmandu.Id, ValidUpdate() with { Name = "" }));

        Assert.Contains("Name", ex.Errors.Keys);
        await _net.UnitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_Missing_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.UpdateAsync(Guid.NewGuid(), ValidUpdate()));
    }

    [Fact]
    public async Task UpdateAsync_ABranchManagerMayChangeOnlyTheirOwnBranch()
    {
        _net.SignInAs(Roles.BranchManager);
        _net.AssignMe(_net.Kathmandu);

        var own = await _sut.UpdateAsync(_net.Kathmandu.Id, ValidUpdate("Own branch"));
        await Assert.ThrowsAsync<ForbiddenException>(() => _sut.UpdateAsync(_net.Pokhara.Id, ValidUpdate("Someone else's branch")));

        Assert.Equal("Own branch", own.Name);
        Assert.NotEqual("Someone else's branch", _net.Pokhara.Name);
    }

    [Fact]
    public async Task UpdateAsync_ABranchManagerWithNoAssignment_IsForbidden()
    {
        _net.SignInAs(Roles.BranchManager);

        await Assert.ThrowsAsync<ForbiddenException>(() => _sut.UpdateAsync(_net.Kathmandu.Id, ValidUpdate()));
    }

    // ---- activate / deactivate -----------------------------------------------------------------------------------

    [Fact]
    public async Task DeactivateAsync_ABranchWithNothingLeftInIt_IsSwitchedOff()
    {
        var response = await _sut.DeactivateAsync(_net.Pokhara.Id);

        Assert.False(response.IsActive);
        Assert.False(_net.Pokhara.IsActive);
        _net.Audit.Received(1).Add(Arg.Is<AuditLog>(a => a.Action == AuditActions.BranchDeactivated));
        await _net.UnitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeactivateAsync_WhileItOperatesActiveWarehouses_IsRefused()
    {
        _net.Warehouses.CountByBranchAsync(Arg.Any<IReadOnlyCollection<Guid>>(), true, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [_net.Kathmandu.Id] = 2 });

        var ex = await Assert.ThrowsAsync<DomainException>(() => _sut.DeactivateAsync(_net.Kathmandu.Id));

        Assert.Contains("2 active warehouse", ex.Message, StringComparison.Ordinal);
        Assert.True(_net.Kathmandu.IsActive);
        await _net.UnitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeactivateAsync_WhileParcelsAreStillOpenAtIt_IsRefused()
    {
        _net.Inventory.CountOpenAtBranchAsync(_net.Kathmandu.Id, Arg.Any<CancellationToken>()).Returns(4);

        var ex = await Assert.ThrowsAsync<DomainException>(() => _sut.DeactivateAsync(_net.Kathmandu.Id));

        Assert.Contains("4 open parcel", ex.Message, StringComparison.Ordinal);
        Assert.True(_net.Kathmandu.IsActive);
    }

    [Fact]
    public async Task DeactivateAsync_WhileStaffAreStillAssigned_IsRefused()
    {
        _net.Assignments.CountActiveByBranchAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [_net.Kathmandu.Id] = 5 });

        var ex = await Assert.ThrowsAsync<DomainException>(() => _sut.DeactivateAsync(_net.Kathmandu.Id));

        Assert.Contains("5 staff", ex.Message, StringComparison.Ordinal);
        Assert.True(_net.Kathmandu.IsActive);
    }

    [Fact]
    public async Task DeactivateAsync_AlreadyInactive_IsAConflict()
    {
        _net.Pokhara.Deactivate();

        await Assert.ThrowsAsync<ConflictException>(() => _sut.DeactivateAsync(_net.Pokhara.Id));
    }

    [Fact]
    public async Task ActivateAsync_SwitchesABranchBackOn_AndRefusesAnActiveOne()
    {
        _net.Pokhara.Deactivate();

        var response = await _sut.ActivateAsync(_net.Pokhara.Id);

        Assert.True(response.IsActive);
        _net.Audit.Received(1).Add(Arg.Is<AuditLog>(a => a.Action == AuditActions.BranchActivated));
        await Assert.ThrowsAsync<ConflictException>(() => _sut.ActivateAsync(_net.Pokhara.Id));
    }

    [Fact]
    public async Task ActivateAndDeactivate_AreForbiddenToAManagerOfAnotherBranch()
    {
        _net.SignInAs(Roles.BranchManager);
        _net.AssignMe(_net.Kathmandu);

        await Assert.ThrowsAsync<ForbiddenException>(() => _sut.DeactivateAsync(_net.Pokhara.Id));
        await Assert.ThrowsAsync<ForbiddenException>(() => _sut.ActivateAsync(_net.Pokhara.Id));
        Assert.True(_net.Pokhara.IsActive);
    }
}
