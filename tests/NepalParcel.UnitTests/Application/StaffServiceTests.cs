using Microsoft.Extensions.Logging.Abstractions;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Network;
using NepalParcel.Contracts.Staff;
using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Identity;
using NSubstitute;

namespace NepalParcel.UnitTests.Application;

public sealed class StaffServiceTests
{
    private readonly NetworkSetup _net = new();
    private readonly IIdentityService _identity = Substitute.For<IIdentityService>();
    private readonly List<StaffAssignment> _added = [];
    private readonly StaffService _sut;

    public StaffServiceTests()
    {
        _net.SignInAs(Roles.SuperAdmin);
        _net.Assignments.When(a => a.Add(Arg.Any<StaffAssignment>())).Do(call => _added.Add(call.Arg<StaffAssignment>()));
        _net.Branches.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Branch> { [_net.Kathmandu.Id] = _net.Kathmandu, [_net.Pokhara.Id] = _net.Pokhara });
        _net.Warehouses.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, NepalParcel.Domain.Warehouses.Warehouse>
            {
                [_net.Hub.Id] = _net.Hub,
                [_net.Hub2.Id] = _net.Hub2,
                [_net.PokharaHub.Id] = _net.PokharaHub,
            });

        _sut = new StaffService(
            _identity,
            _net.Assignments,
            _net.Branches,
            _net.Warehouses,
            _net.Access,
            _net.Audit,
            _net.UnitOfWork,
            _net.CurrentUser,
            _net.Client,
            _net.Clock,
            new StaffQueryValidator(),
            new AssignStaffRequestValidator(),
            NullLogger<StaffService>.Instance);
    }

    private UserInfo Person(string role = Roles.WarehouseStaff, bool active = true)
    {
        var user = new UserInfo(Guid.NewGuid(), "staff@nepalparcel.test", "Sita Staff", "9841000010", [role], active, TestData.Now);
        _identity.FindByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        return user;
    }

    // ---- assigning -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Assign_StartsAnAssignment_AtTheBranchAndWarehouse_AndAudits()
    {
        var user = Person();

        var response = await _sut.AssignAsync(user.Id, new AssignStaffRequest(_net.Kathmandu.Id, _net.Hub.Id));

        var created = Assert.Single(_added);
        Assert.Equal(user.Id, created.UserId);
        Assert.Equal(_net.Kathmandu.Id, created.BranchId);
        Assert.Equal(_net.Hub.Id, created.WarehouseId);
        Assert.Equal(_net.UserId, created.AssignedByUserId);
        Assert.Equal(_net.Clock.GetUtcNow(), created.AssignedAtUtc);
        Assert.Equal("Kathmandu Main", response.Assignment!.BranchName);
        Assert.Equal("Kathmandu Hub", response.Assignment.WarehouseName);
        _net.Audit.Received(1).Add(Arg.Is<AuditLog>(a => a.Action == AuditActions.StaffAssigned));
        await _net.UnitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Assign_ToAWholeBranch_NeedsNoWarehouse()
    {
        var user = Person(Roles.BookingStaff);

        var response = await _sut.AssignAsync(user.Id, new AssignStaffRequest(_net.Pokhara.Id));

        Assert.Null(response.Assignment!.WarehouseId);
        Assert.Null(response.Assignment.WarehouseName);
    }

    [Fact]
    public async Task Assign_MovingSomeone_EndsTheOldAssignmentFirst_AndKeepsIt()
    {
        var user = Person();
        var old = StaffAssignment.Create(user.Id, _net.Pokhara.Id, null, TestData.Now, null);
        _net.Assignments.GetActiveByUserIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(old);

        await _sut.AssignAsync(user.Id, new AssignStaffRequest(_net.Kathmandu.Id, _net.Hub.Id));

        Assert.False(old.IsActive);
        Assert.Equal(_net.Clock.GetUtcNow(), old.EndedAtUtc);
        Assert.Equal(_net.UserId, old.EndedByUserId);
        Assert.Single(_added);
        await _net.UnitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Assign_TheSamePlaceAgain_ChangesNothing()
    {
        var user = Person();
        var current = StaffAssignment.Create(user.Id, _net.Kathmandu.Id, _net.Hub.Id, TestData.Now, null);
        _net.Assignments.GetActiveByUserIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(current);

        var response = await _sut.AssignAsync(user.Id, new AssignStaffRequest(_net.Kathmandu.Id, _net.Hub.Id));

        Assert.True(current.IsActive);
        Assert.Empty(_added);
        Assert.Equal(current.Id, response.Assignment!.AssignmentId);
        await _net.UnitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Assign_ACustomerOrAnInactiveAccount_IsRefused()
    {
        var customer = Person(Roles.Customer);
        var off = Person(active: false);

        await Assert.ThrowsAsync<DomainException>(() => _sut.AssignAsync(customer.Id, new AssignStaffRequest(_net.Kathmandu.Id)));
        await Assert.ThrowsAsync<DomainException>(() => _sut.AssignAsync(off.Id, new AssignStaffRequest(_net.Kathmandu.Id)));

        Assert.Empty(_added);
    }

    [Fact]
    public async Task Assign_UnknownPersonBranchOrWarehouse_IsNotFound()
    {
        var user = Person();

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.AssignAsync(Guid.NewGuid(), new AssignStaffRequest(_net.Kathmandu.Id)));
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.AssignAsync(user.Id, new AssignStaffRequest(Guid.NewGuid())));
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.AssignAsync(user.Id, new AssignStaffRequest(_net.Kathmandu.Id, Guid.NewGuid())));
    }

    [Fact]
    public async Task Assign_ToAnInactiveBranchOrWarehouse_OrAWarehouseOfAnotherBranch_IsRefused()
    {
        var user = Person();

        await Assert.ThrowsAsync<DomainException>(() => _sut.AssignAsync(user.Id, new AssignStaffRequest(_net.Kathmandu.Id, _net.PokharaHub.Id)));

        _net.Hub.Deactivate();
        await Assert.ThrowsAsync<DomainException>(() => _sut.AssignAsync(user.Id, new AssignStaffRequest(_net.Kathmandu.Id, _net.Hub.Id)));

        _net.Pokhara.Deactivate();
        await Assert.ThrowsAsync<DomainException>(() => _sut.AssignAsync(user.Id, new AssignStaffRequest(_net.Pokhara.Id)));

        Assert.Empty(_added);
    }

    [Fact]
    public async Task Assign_WithoutABranch_FailsValidation()
    {
        var user = Person();

        await Assert.ThrowsAsync<RequestValidationException>(() => _sut.AssignAsync(user.Id, new AssignStaffRequest(Guid.Empty)));
    }

    // ---- branch managers are limited to their own branch -----------------------------------------------------------

    [Fact]
    public async Task ABranchManager_CanAssignStaffIntoTheirOwnBranch()
    {
        _net.SignInAs(Roles.BranchManager);
        _net.AssignMe(_net.Kathmandu);
        var user = Person();

        var response = await _sut.AssignAsync(user.Id, new AssignStaffRequest(_net.Kathmandu.Id, _net.Hub2.Id));

        Assert.Equal(_net.Hub2.Id, response.Assignment!.WarehouseId);
    }

    [Fact]
    public async Task ABranchManager_CannotAssignStaffIntoAnotherBranch()
    {
        _net.SignInAs(Roles.BranchManager);
        _net.AssignMe(_net.Kathmandu);
        var user = Person();

        await Assert.ThrowsAsync<ForbiddenException>(() => _sut.AssignAsync(user.Id, new AssignStaffRequest(_net.Pokhara.Id)));

        Assert.Empty(_added);
    }

    [Fact]
    public async Task ABranchManager_CannotMoveStaffAwayFromAnotherBranch()
    {
        _net.SignInAs(Roles.BranchManager);
        _net.AssignMe(_net.Kathmandu);
        var user = Person();
        var elsewhere = StaffAssignment.Create(user.Id, _net.Pokhara.Id, null, TestData.Now, null);
        _net.Assignments.GetActiveByUserIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(elsewhere);

        await Assert.ThrowsAsync<ForbiddenException>(() => _sut.AssignAsync(user.Id, new AssignStaffRequest(_net.Kathmandu.Id)));

        Assert.True(elsewhere.IsActive);
    }

    [Fact]
    public async Task ABranchManager_CannotChangeASuperAdministratorsAssignment()
    {
        _net.SignInAs(Roles.BranchManager);
        _net.AssignMe(_net.Kathmandu);
        var admin = Person(Roles.SuperAdmin);

        await Assert.ThrowsAsync<ForbiddenException>(() => _sut.AssignAsync(admin.Id, new AssignStaffRequest(_net.Kathmandu.Id)));
    }

    [Fact]
    public async Task AnUnassignedBranchManager_CannotAssignAnyone()
    {
        _net.SignInAs(Roles.BranchManager);
        var user = Person();

        await Assert.ThrowsAsync<ForbiddenException>(() => _sut.AssignAsync(user.Id, new AssignStaffRequest(_net.Kathmandu.Id)));
    }

    [Theory]
    [InlineData(Roles.WarehouseStaff)]
    [InlineData(Roles.BookingStaff)]
    public async Task OtherStaff_CannotAssignAnyone(string role)
    {
        _net.SignInAs(role);
        var user = Person();

        await Assert.ThrowsAsync<ForbiddenException>(() => _sut.AssignAsync(user.Id, new AssignStaffRequest(_net.Kathmandu.Id)));
    }

    // ---- unassigning ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Unassign_EndsTheAssignment_AndAudits()
    {
        var user = Person();
        var current = StaffAssignment.Create(user.Id, _net.Kathmandu.Id, _net.Hub.Id, TestData.Now, null);
        _net.Assignments.GetActiveByUserIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(current);

        await _sut.UnassignAsync(user.Id);

        Assert.False(current.IsActive);
        Assert.Equal(_net.UserId, current.EndedByUserId);
        _net.Audit.Received(1).Add(Arg.Is<AuditLog>(a => a.Action == AuditActions.StaffUnassigned));
        await _net.UnitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unassign_SomeoneWithNoAssignment_IsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.UnassignAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Unassign_ABranchManagerMayOnlyEndAssignmentsAtTheirOwnBranch()
    {
        _net.SignInAs(Roles.BranchManager);
        _net.AssignMe(_net.Kathmandu);
        var user = Person();
        var elsewhere = StaffAssignment.Create(user.Id, _net.Pokhara.Id, null, TestData.Now, null);
        _net.Assignments.GetActiveByUserIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(elsewhere);

        await Assert.ThrowsAsync<ForbiddenException>(() => _sut.UnassignAsync(user.Id));

        Assert.True(elsewhere.IsActive);
    }

    // ---- my assignment -------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetMine_WithNoAssignment_ReturnsNull()
    {
        var response = await _sut.GetMineAsync();

        Assert.Null(response.Assignment);
    }

    [Fact]
    public async Task GetMine_ReturnsTheBranchAndWarehouseNames()
    {
        _net.AssignMe(_net.Kathmandu, _net.Hub2);

        var response = await _sut.GetMineAsync();

        Assert.Equal("Kathmandu Main", response.Assignment!.BranchName);
        Assert.Equal("Kathmandu East Hub", response.Assignment.WarehouseName);
    }

    // ---- listing -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task List_ByBranch_OnlyAsksForPeopleAssignedThere()
    {
        var inBranch = Guid.NewGuid();
        _net.Assignments.GetActiveUserIdsAsync(_net.Kathmandu.Id, null, Arg.Any<CancellationToken>()).Returns(new List<Guid> { inBranch });
        _identity
            .ListStaffAsync(Arg.Any<StaffUserFilter>(), 1, 20, Arg.Any<CancellationToken>())
            .Returns((new List<UserInfo> { new(inBranch, "a@x.test", "Anil", null, [Roles.WarehouseStaff], true, TestData.Now) }, 1));
        _net.Assignments.GetActiveByUserIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, StaffAssignment> { [inBranch] = StaffAssignment.Create(inBranch, _net.Kathmandu.Id, null, TestData.Now, null) });

        var page = await _sut.ListAsync(new StaffQuery { BranchId = _net.Kathmandu.Id, Search = " an " });

        var item = Assert.Single(page.Items);
        Assert.Equal("Kathmandu Main", item.Assignment!.BranchName);
        await _identity.Received(1).ListStaffAsync(
            Arg.Is<StaffUserFilter>(f => f.Search == "an" && f.OnlyUserIds != null && f.OnlyUserIds.Contains(inBranch)),
            1,
            20,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_ForABranchNobodyWorksAt_IsEmpty_WithoutAskingForAccounts()
    {
        _net.Assignments.GetActiveUserIdsAsync(Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(new List<Guid>());

        var page = await _sut.ListAsync(new StaffQuery { BranchId = _net.Pokhara.Id });

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        await _identity.DidNotReceiveWithAnyArgs().ListStaffAsync(default!, default, default, default);
    }

    [Fact]
    public async Task List_Unassigned_ExcludesEveryoneWhoWorksSomewhere()
    {
        var busy = Guid.NewGuid();
        _net.Assignments.GetActiveUserIdsAsync(null, null, Arg.Any<CancellationToken>()).Returns(new List<Guid> { busy });
        _identity
            .ListStaffAsync(Arg.Any<StaffUserFilter>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((new List<UserInfo>(), 0));

        await _sut.ListAsync(new StaffQuery { Unassigned = true });

        await _identity.Received(1).ListStaffAsync(
            Arg.Is<StaffUserFilter>(f => f.ExcludeUserIds != null && f.ExcludeUserIds.Contains(busy) && f.OnlyUserIds == null),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_UnknownRole_FailsValidation()
    {
        await Assert.ThrowsAsync<RequestValidationException>(() => _sut.ListAsync(new StaffQuery { Role = "Wizard" }));
    }
}
