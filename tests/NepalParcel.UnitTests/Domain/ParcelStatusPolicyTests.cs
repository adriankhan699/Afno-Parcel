using NepalParcel.Domain.Common;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Parcels;

namespace NepalParcel.UnitTests.Domain;

/// <summary>The workflow arrows, who may follow them, and what each step must record.</summary>
public sealed class ParcelStatusPolicyTests
{
    private static readonly IReadOnlyDictionary<ParcelStatusCode, ParcelStatusCode[]> Expected = new Dictionary<ParcelStatusCode, ParcelStatusCode[]>
    {
        [ParcelStatusCode.BookingCreated] = [ParcelStatusCode.PickedUp, ParcelStatusCode.Cancelled],
        [ParcelStatusCode.PickedUp] = [ParcelStatusCode.Shipped, ParcelStatusCode.Cancelled],
        [ParcelStatusCode.Shipped] = [ParcelStatusCode.InTransit, ParcelStatusCode.ArrivedAtWarehouse, ParcelStatusCode.OutForDelivery],
        [ParcelStatusCode.InTransit] = [ParcelStatusCode.ArrivedAtWarehouse, ParcelStatusCode.OutForDelivery, ParcelStatusCode.Returned],
        [ParcelStatusCode.ArrivedAtWarehouse] = [ParcelStatusCode.InTransit, ParcelStatusCode.OutForDelivery, ParcelStatusCode.Returned],
        [ParcelStatusCode.OutForDelivery] = [ParcelStatusCode.Delivered, ParcelStatusCode.DeliveryFailed],
        [ParcelStatusCode.DeliveryFailed] = [ParcelStatusCode.OutForDelivery, ParcelStatusCode.ArrivedAtWarehouse, ParcelStatusCode.InTransit, ParcelStatusCode.Returned],
        [ParcelStatusCode.Delivered] = [],
        [ParcelStatusCode.Returned] = [],
        [ParcelStatusCode.Cancelled] = [],
    };

    public static TheoryData<ParcelStatusCode, ParcelStatusCode, bool> EveryPair()
    {
        var data = new TheoryData<ParcelStatusCode, ParcelStatusCode, bool>();
        foreach (var from in Enum.GetValues<ParcelStatusCode>())
        {
            foreach (var to in Enum.GetValues<ParcelStatusCode>())
            {
                data.Add(from, to, Expected[from].Contains(to));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryPair))]
    public void Workflow_AllowsExactlyTheDefinedMoves(ParcelStatusCode from, ParcelStatusCode to, bool allowed) =>
        Assert.Equal(allowed, ParcelStatusWorkflow.CanTransition(from, to));

    [Fact]
    public void NoStatus_CanMoveToItself_OrBackToBookingCreated()
    {
        foreach (var status in Enum.GetValues<ParcelStatusCode>())
        {
            Assert.False(ParcelStatusWorkflow.CanTransition(status, status));
            Assert.False(ParcelStatusWorkflow.CanTransition(status, ParcelStatusCode.BookingCreated));
        }
    }

    [Fact]
    public void EveryNonTerminalStatus_HasAWayForward_AndTerminalOnesHaveNone()
    {
        foreach (var entry in ParcelStatusCatalog.All)
        {
            Assert.Equal(entry.IsTerminal, ParcelStatusWorkflow.IsTerminal(entry.Code));
            Assert.Equal(entry.IsTerminal, ParcelStatusWorkflow.NextStatuses(entry.Code).Count == 0);
        }
    }

    [Fact]
    public void EveryStatus_IsReachableFromBookingCreated()
    {
        var reached = new HashSet<ParcelStatusCode> { ParcelStatusCode.BookingCreated };
        var queue = new Queue<ParcelStatusCode>([ParcelStatusCode.BookingCreated]);
        while (queue.Count > 0)
        {
            foreach (var next in ParcelStatusWorkflow.NextStatuses(queue.Dequeue()))
            {
                if (reached.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        Assert.Equal(Enum.GetValues<ParcelStatusCode>().Length, reached.Count);
    }

    [Fact]
    public void Catalog_GivesEveryStatusANameDescriptionAndAPublicMessage()
    {
        foreach (var entry in ParcelStatusCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Name));
            Assert.False(string.IsNullOrWhiteSpace(entry.Description));
            Assert.EndsWith(".", entry.PublicMessage, StringComparison.Ordinal);
        }

        Assert.Equal("Arrived at warehouse", ParcelStatusCatalog.Get(ParcelStatusCode.ArrivedAtWarehouse).Name);
        Assert.Equal("Booking created", ParcelStatusCatalog.NameOf(1));
    }

    [Fact]
    public void PublicMessages_NeverContainPlaceholdersForPersonalData()
    {
        foreach (var entry in ParcelStatusCatalog.All)
        {
            Assert.DoesNotContain("{", entry.PublicMessage, StringComparison.Ordinal);
            Assert.DoesNotContain("@", entry.PublicMessage, StringComparison.Ordinal);
        }
    }

    // ---- who may do what ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Roles.SuperAdmin, ParcelStatusCode.PickedUp, true)]
    [InlineData(Roles.BookingStaff, ParcelStatusCode.PickedUp, true)]
    [InlineData(Roles.DeliveryAgent, ParcelStatusCode.PickedUp, true)]
    [InlineData(Roles.WarehouseStaff, ParcelStatusCode.PickedUp, false)]
    [InlineData(Roles.BookingStaff, ParcelStatusCode.Shipped, true)]
    [InlineData(Roles.WarehouseStaff, ParcelStatusCode.Shipped, true)]
    [InlineData(Roles.DeliveryAgent, ParcelStatusCode.Shipped, false)]
    [InlineData(Roles.WarehouseStaff, ParcelStatusCode.InTransit, true)]
    [InlineData(Roles.BookingStaff, ParcelStatusCode.InTransit, false)]
    [InlineData(Roles.DeliveryAgent, ParcelStatusCode.InTransit, false)]
    [InlineData(Roles.WarehouseStaff, ParcelStatusCode.ArrivedAtWarehouse, true)]
    [InlineData(Roles.BookingStaff, ParcelStatusCode.ArrivedAtWarehouse, false)]
    [InlineData(Roles.DeliveryAgent, ParcelStatusCode.ArrivedAtWarehouse, false)]
    [InlineData(Roles.WarehouseStaff, ParcelStatusCode.OutForDelivery, true)]
    [InlineData(Roles.DeliveryAgent, ParcelStatusCode.OutForDelivery, true)]
    [InlineData(Roles.BookingStaff, ParcelStatusCode.OutForDelivery, false)]
    [InlineData(Roles.DeliveryAgent, ParcelStatusCode.Delivered, true)]
    [InlineData(Roles.DeliveryAgent, ParcelStatusCode.DeliveryFailed, true)]
    [InlineData(Roles.WarehouseStaff, ParcelStatusCode.Delivered, false)]
    [InlineData(Roles.BookingStaff, ParcelStatusCode.Delivered, false)]
    [InlineData(Roles.WarehouseStaff, ParcelStatusCode.Returned, true)]
    [InlineData(Roles.DeliveryAgent, ParcelStatusCode.Returned, false)]
    [InlineData(Roles.BranchManager, ParcelStatusCode.Delivered, true)]
    [InlineData(Roles.BranchManager, ParcelStatusCode.Returned, true)]
    public void Permissions_MatchTheJobEachRoleDoes(string role, ParcelStatusCode to, bool allowed) =>
        Assert.Equal(allowed, ParcelStatusPermissions.CanSet(role, to));

    [Theory]
    [InlineData(Roles.Customer)]
    [InlineData(Roles.Accountant)]
    public void CustomersAndAccountants_CanNeverChangeAStatus(string role)
    {
        foreach (var status in Enum.GetValues<ParcelStatusCode>())
        {
            Assert.False(ParcelStatusPermissions.CanSet(role, status));
        }
    }

    [Fact]
    public void NobodyCanSetBookingCreatedOrCancelled_ThroughTheStatusApi()
    {
        foreach (var role in Roles.All)
        {
            Assert.False(ParcelStatusPermissions.CanSet(role, ParcelStatusCode.BookingCreated));
            Assert.False(ParcelStatusPermissions.CanSet(role, ParcelStatusCode.Cancelled));
        }

        Assert.DoesNotContain(ParcelStatusCode.BookingCreated, ParcelStatusPermissions.Settable);
        Assert.DoesNotContain(ParcelStatusCode.Cancelled, ParcelStatusPermissions.Settable);
        Assert.Equal(8, ParcelStatusPermissions.Settable.Count);
    }

    [Fact]
    public void SuperAdmin_MayMakeEverySettableChange()
    {
        foreach (var status in ParcelStatusPermissions.Settable)
        {
            Assert.True(ParcelStatusPermissions.CanSet(Roles.SuperAdmin, status));
        }
    }

    // ---- what each step must record ----------------------------------------------------------------------------

    [Theory]
    [InlineData(ParcelStatusCode.DeliveryFailed, true)]
    [InlineData(ParcelStatusCode.Returned, true)]
    [InlineData(ParcelStatusCode.Cancelled, true)]
    [InlineData(ParcelStatusCode.PickedUp, false)]
    [InlineData(ParcelStatusCode.Shipped, false)]
    [InlineData(ParcelStatusCode.InTransit, false)]
    [InlineData(ParcelStatusCode.Delivered, false)]
    public void Remarks_AreRequiredOnlyWhereSomethingWentWrong(ParcelStatusCode status, bool required) =>
        Assert.Equal(required, ParcelStatusRules.RequiresRemarks(status));

    [Theory]
    [InlineData(ParcelStatusCode.Shipped, LocationRule.Branch)]
    [InlineData(ParcelStatusCode.ArrivedAtWarehouse, LocationRule.Warehouse)]
    [InlineData(ParcelStatusCode.OutForDelivery, LocationRule.BranchOrWarehouse)]
    [InlineData(ParcelStatusCode.Returned, LocationRule.BranchOrWarehouse)]
    [InlineData(ParcelStatusCode.PickedUp, LocationRule.Optional)]
    [InlineData(ParcelStatusCode.InTransit, LocationRule.Optional)]
    [InlineData(ParcelStatusCode.Delivered, LocationRule.Optional)]
    public void Location_RuleDependsOnTheStatus(ParcelStatusCode status, LocationRule expected) =>
        Assert.Equal(expected, ParcelStatusRules.LocationFor(status));

    [Fact]
    public void Validate_AcceptsCompleteInformation_AndRejectsGaps()
    {
        var branch = Guid.NewGuid();
        var warehouse = Guid.NewGuid();

        ParcelStatusRules.Validate(ParcelStatusCode.Shipped, null, branch, null);
        ParcelStatusRules.Validate(ParcelStatusCode.ArrivedAtWarehouse, null, null, warehouse);
        ParcelStatusRules.Validate(ParcelStatusCode.OutForDelivery, null, branch, null);
        ParcelStatusRules.Validate(ParcelStatusCode.OutForDelivery, null, null, warehouse);
        ParcelStatusRules.Validate(ParcelStatusCode.DeliveryFailed, "Not home", null, null);
        ParcelStatusRules.Validate(ParcelStatusCode.Delivered, null, null, null);

        Assert.Throws<DomainException>(() => ParcelStatusRules.Validate(ParcelStatusCode.Shipped, null, null, null));
        Assert.Throws<DomainException>(() => ParcelStatusRules.Validate(ParcelStatusCode.Shipped, null, null, warehouse));
        Assert.Throws<DomainException>(() => ParcelStatusRules.Validate(ParcelStatusCode.ArrivedAtWarehouse, null, branch, null));
        Assert.Throws<DomainException>(() => ParcelStatusRules.Validate(ParcelStatusCode.OutForDelivery, null, null, null));
        Assert.Throws<DomainException>(() => ParcelStatusRules.Validate(ParcelStatusCode.OutForDelivery, null, branch, warehouse));
        Assert.Throws<DomainException>(() => ParcelStatusRules.Validate(ParcelStatusCode.DeliveryFailed, " ", null, null));
        Assert.Throws<DomainException>(() => ParcelStatusRules.Validate(ParcelStatusCode.Returned, null, branch, null));
    }
}
