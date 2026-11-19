using NepalParcel.Application.Auth;
using NepalParcel.Application.Branches;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Delivery;
using NepalParcel.Application.Network;
using NepalParcel.Application.Notifications;
using NepalParcel.Application.Tracking;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Warehouses;
using NSubstitute;

namespace NepalParcel.UnitTests.Application;

/// <summary>
/// The collaborators every branch, warehouse, staff and scan test needs: substitute repositories, a signed-in user, and a small
/// network (two branches, three warehouses in the first one and one in the second).
/// </summary>
internal sealed class NetworkSetup
{
    public NetworkSetup()
    {
        CurrentUser.UserId.Returns(UserId);
        CurrentUser.UserName.Returns("Hari Staff");

        foreach (var branch in new[] { Kathmandu, Pokhara })
        {
            Branches.GetByIdAsync(branch.Id, Arg.Any<CancellationToken>()).Returns(branch);
        }

        Hub = Warehouse.Create(Kathmandu.Id, "KTMW1", "Kathmandu Hub", TestData.KathmanduAddress(), "9841000001");
        Hub2 = Warehouse.Create(Kathmandu.Id, "KTMW2", "Kathmandu East Hub", TestData.KathmanduAddress(), "9841000002", 200);
        PokharaHub = Warehouse.Create(Pokhara.Id, "PKRW1", "Pokhara Hub", TestData.PokharaAddress(), "9856000001");
        foreach (var warehouse in new[] { Hub, Hub2, PokharaHub })
        {
            Warehouses.GetByIdAsync(warehouse.Id, Arg.Any<CancellationToken>()).Returns(warehouse);
        }

        Access = new WorkplaceAccess(CurrentUser, Assignments);
        Changer = new ParcelStatusChanger(Branches, Warehouses, Movements, Deliveries, Audit, CurrentUser, Client, Clock, Access, Notifications);
    }

    public INotificationPlanner Notifications { get; } = Substitute.For<INotificationPlanner>();

    public IBranchRepository Branches { get; } = Substitute.For<IBranchRepository>();

    public IWarehouseRepository Warehouses { get; } = Substitute.For<IWarehouseRepository>();

    public IMovementRepository Movements { get; } = Substitute.For<IMovementRepository>();

    public IStaffAssignmentRepository Assignments { get; } = Substitute.For<IStaffAssignmentRepository>();

    public IInventoryRepository Inventory { get; } = Substitute.For<IInventoryRepository>();

    public IDeliveryAssignmentRepository Deliveries { get; } = Substitute.For<IDeliveryAssignmentRepository>();

    public IAuditLogRepository Audit { get; } = Substitute.For<IAuditLogRepository>();

    public IUnitOfWork UnitOfWork { get; } = Substitute.For<IUnitOfWork>();

    public ICurrentUser CurrentUser { get; } = Substitute.For<ICurrentUser>();

    public IClientInfo Client { get; } = Substitute.For<IClientInfo>();

    public FixedClock Clock { get; } = new(TestData.Now.AddDays(10));

    public Guid UserId { get; } = Guid.NewGuid();

    public Branch Kathmandu { get; } = Branch.Create("KTM01", "Kathmandu Main", BranchType.Branch, TestData.KathmanduAddress(), "9841000000");

    public Branch Pokhara { get; } = Branch.Create("PKR01", "Pokhara Branch", BranchType.Branch, TestData.PokharaAddress(), "9856000000");

    /// <summary>A warehouse of Kathmandu Main.</summary>
    public Warehouse Hub { get; }

    /// <summary>A second warehouse of Kathmandu Main, with a capacity of 200 parcels.</summary>
    public Warehouse Hub2 { get; }

    /// <summary>A warehouse of the Pokhara branch.</summary>
    public Warehouse PokharaHub { get; }

    public WorkplaceAccess Access { get; }

    public ParcelStatusChanger Changer { get; }

    public void SignInAs(string role)
    {
        CurrentUser.IsInRole(Arg.Any<string>()).Returns(false);
        CurrentUser.IsInRole(role).Returns(true);
    }

    /// <summary>Makes the signed-in user work at a branch, and optionally one warehouse of it.</summary>
    public StaffAssignment AssignMe(Branch branch, Warehouse? warehouse = null)
    {
        var assignment = StaffAssignment.Create(UserId, branch.Id, warehouse?.Id, TestData.Now, null);
        Assignments.GetActiveByUserIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(assignment);
        return assignment;
    }
}

