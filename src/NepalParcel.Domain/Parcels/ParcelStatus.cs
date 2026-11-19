using NepalParcel.Domain.Common;
using NepalParcel.Domain.Identity;

namespace NepalParcel.Domain.Parcels;

/// <summary>
/// Stable ids of the seeded parcel statuses (also the primary keys of the ParcelStatuses lookup table).
/// Ids are never reused or renumbered; 9 is intentionally unused (it belonged to a status that was removed in Phase 4).
/// </summary>
public enum ParcelStatusCode
{
    BookingCreated = 1,
    PickedUp = 2,
    Shipped = 3,
    InTransit = 4,
    ArrivedAtWarehouse = 5,
    OutForDelivery = 6,
    Delivered = 7,
    DeliveryFailed = 8,
    Returned = 10,
    Cancelled = 11,
}

/// <summary>Lookup row describing one parcel status. Seeded by migration; do not change ids.</summary>
public sealed class ParcelStatus
{
    public const int CodeMaxLength = 40;

    private ParcelStatus()
    {
        Code = string.Empty;
        Name = string.Empty;
        Description = string.Empty;
    }

    public int Id { get; private set; }

    public string Code { get; private set; }

    public string Name { get; private set; }

    public string Description { get; private set; }

    public int SortOrder { get; private set; }

    /// <summary>No further status changes are allowed once a parcel reaches a terminal status.</summary>
    public bool IsTerminal { get; private set; }

    /// <summary>Whether the status may be shown to anyone who knows the tracking number.</summary>
    public bool IsPublic { get; private set; }
}

/// <summary>The initial data for <see cref="ParcelStatus"/>, plus the wording shown to the public.</summary>
public static class ParcelStatusCatalog
{
    /// <param name="PublicMessage">A standard sentence safe to show to anyone with the tracking number. Free-text staff remarks are never shown publicly.</param>
    public sealed record Entry(ParcelStatusCode Code, string Name, string Description, bool IsTerminal, string PublicMessage);

    public static IReadOnlyList<Entry> All { get; } =
    [
        new(ParcelStatusCode.BookingCreated, "Booking created", "Booking confirmed; waiting to be handed over.", false,
            "Your booking has been created."),
        new(ParcelStatusCode.PickedUp, "Picked up", "Collected from the sender or received at the counter.", false,
            "The parcel has been picked up."),
        new(ParcelStatusCode.Shipped, "Shipped", "Dispatched from the origin branch into the network.", false,
            "The parcel has left the origin branch."),
        new(ParcelStatusCode.InTransit, "In transit", "On its way between branches or hubs.", false,
            "The parcel is on its way."),
        new(ParcelStatusCode.ArrivedAtWarehouse, "Arrived at warehouse", "Received and scanned at a warehouse or sorting hub.", false,
            "The parcel has arrived at a warehouse."),
        new(ParcelStatusCode.OutForDelivery, "Out for delivery", "With a delivery agent for the final leg.", false,
            "The parcel is out for delivery."),
        new(ParcelStatusCode.Delivered, "Delivered", "Handed over to the receiver.", true,
            "The parcel has been delivered."),
        new(ParcelStatusCode.DeliveryFailed, "Delivery attempt failed", "Receiver unavailable or address problem.", false,
            "A delivery attempt was unsuccessful."),
        new(ParcelStatusCode.Returned, "Returned", "Returned to the sender.", true,
            "The parcel has been returned to the sender."),
        new(ParcelStatusCode.Cancelled, "Cancelled", "Booking cancelled before dispatch.", true,
            "The booking was cancelled."),
    ];

    public static Entry Get(ParcelStatusCode code) => All.Single(e => e.Code == code);

    public static string NameOf(int statusId) =>
        All.FirstOrDefault(e => (int)e.Code == statusId)?.Name ?? statusId.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Defines which status changes are allowed. A parcel can only ever move along these arrows.</summary>
public static class ParcelStatusWorkflow
{
    private static readonly Dictionary<ParcelStatusCode, ParcelStatusCode[]> Allowed = new()
    {
        [ParcelStatusCode.BookingCreated] = [ParcelStatusCode.PickedUp, ParcelStatusCode.Cancelled],
        [ParcelStatusCode.PickedUp] = [ParcelStatusCode.Shipped, ParcelStatusCode.Cancelled],
        [ParcelStatusCode.Shipped] =
            [ParcelStatusCode.InTransit, ParcelStatusCode.ArrivedAtWarehouse, ParcelStatusCode.OutForDelivery],
        [ParcelStatusCode.InTransit] =
            [ParcelStatusCode.ArrivedAtWarehouse, ParcelStatusCode.OutForDelivery, ParcelStatusCode.Returned],
        [ParcelStatusCode.ArrivedAtWarehouse] =
            [ParcelStatusCode.InTransit, ParcelStatusCode.OutForDelivery, ParcelStatusCode.Returned],
        [ParcelStatusCode.OutForDelivery] = [ParcelStatusCode.Delivered, ParcelStatusCode.DeliveryFailed],
        [ParcelStatusCode.DeliveryFailed] =
            [ParcelStatusCode.OutForDelivery, ParcelStatusCode.ArrivedAtWarehouse, ParcelStatusCode.InTransit, ParcelStatusCode.Returned],
    };

    /// <summary>The statuses reachable from <paramref name="from"/> (empty for terminal statuses).</summary>
    public static IReadOnlyCollection<ParcelStatusCode> NextStatuses(ParcelStatusCode from) =>
        Allowed.TryGetValue(from, out var next) ? next : [];

    public static bool CanTransition(ParcelStatusCode from, ParcelStatusCode to) => NextStatuses(from).Contains(to);

    public static bool IsTerminal(ParcelStatusCode status) => NextStatuses(status).Count == 0;
}

/// <summary>Where a status change has to be recorded.</summary>
public enum LocationRule
{
    /// <summary>A branch or warehouse may be given but is not needed.</summary>
    Optional = 0,

    /// <summary>A branch is required (and no warehouse).</summary>
    Branch = 1,

    /// <summary>A warehouse is required (and no branch).</summary>
    Warehouse = 2,

    /// <summary>Exactly one of a branch or a warehouse is required.</summary>
    BranchOrWarehouse = 3,
}

/// <summary>Per-status data rules: remarks and the branch or warehouse that must be recorded.</summary>
public static class ParcelStatusRules
{
    public const int RemarksMaxLength = 500;

    /// <summary>Failures, returns and cancellations must always say why.</summary>
    public static bool RequiresRemarks(ParcelStatusCode to) =>
        to is ParcelStatusCode.DeliveryFailed or ParcelStatusCode.Returned or ParcelStatusCode.Cancelled;

    public static LocationRule LocationFor(ParcelStatusCode to) => to switch
    {
        ParcelStatusCode.Shipped => LocationRule.Branch,
        ParcelStatusCode.ArrivedAtWarehouse => LocationRule.Warehouse,
        ParcelStatusCode.OutForDelivery or ParcelStatusCode.Returned => LocationRule.BranchOrWarehouse,
        _ => LocationRule.Optional,
    };

    /// <summary>Throws <see cref="DomainException"/> when the remarks or location do not satisfy the rules for <paramref name="to"/>.</summary>
    public static void Validate(ParcelStatusCode to, string? remarks, Guid? branchId, Guid? warehouseId)
    {
        var name = ParcelStatusCatalog.Get(to).Name;

        if (RequiresRemarks(to) && string.IsNullOrWhiteSpace(remarks))
        {
            throw new DomainException($"Remarks are required when a parcel is marked '{name}'.");
        }

        if (branchId is not null && warehouseId is not null)
        {
            throw new DomainException("Give a branch or a warehouse, not both.");
        }

        var rule = LocationFor(to);
        if (rule == LocationRule.Branch && branchId is null)
        {
            throw new DomainException($"A branch is required when a parcel is marked '{name}'.");
        }

        if (rule == LocationRule.Warehouse && warehouseId is null)
        {
            throw new DomainException($"A warehouse is required when a parcel is marked '{name}'.");
        }

        if (rule == LocationRule.BranchOrWarehouse && branchId is null && warehouseId is null)
        {
            throw new DomainException($"A branch or a warehouse is required when a parcel is marked '{name}'.");
        }

        if (rule == LocationRule.Branch && warehouseId is not null)
        {
            throw new DomainException($"'{name}' is recorded at a branch, not a warehouse.");
        }

        if (rule == LocationRule.Warehouse && branchId is not null)
        {
            throw new DomainException($"'{name}' is recorded at a warehouse, not a branch.");
        }
    }
}

/// <summary>
/// Which roles may move a parcel into which status through the status API. Cancelling has its own endpoint and rules.
/// Delivery agents are deliberately absent: they can only act on parcels assigned to them, through the agent API, which also
/// collects the proof of delivery and the cash.
/// </summary>
public static class ParcelStatusPermissions
{
    private static readonly Dictionary<ParcelStatusCode, string[]> Allowed = new()
    {
        [ParcelStatusCode.PickedUp] = [Roles.SuperAdmin, Roles.BranchManager, Roles.BookingStaff],
        [ParcelStatusCode.Shipped] = [Roles.SuperAdmin, Roles.BranchManager, Roles.BookingStaff, Roles.WarehouseStaff],
        [ParcelStatusCode.InTransit] = [Roles.SuperAdmin, Roles.BranchManager, Roles.WarehouseStaff],
        [ParcelStatusCode.ArrivedAtWarehouse] = [Roles.SuperAdmin, Roles.BranchManager, Roles.WarehouseStaff],
        [ParcelStatusCode.OutForDelivery] = [Roles.SuperAdmin, Roles.BranchManager, Roles.WarehouseStaff],
        [ParcelStatusCode.Delivered] = [Roles.SuperAdmin, Roles.BranchManager],
        [ParcelStatusCode.DeliveryFailed] = [Roles.SuperAdmin, Roles.BranchManager],
        [ParcelStatusCode.Returned] = [Roles.SuperAdmin, Roles.BranchManager, Roles.WarehouseStaff],
    };

    /// <summary>Whether a user in <paramref name="role"/> may set <paramref name="to"/> through the status or scan API.</summary>
    public static bool CanSet(string role, ParcelStatusCode to) =>
        Allowed.TryGetValue(to, out var roles) && roles.Contains(role, StringComparer.Ordinal);

    /// <summary>Statuses that can be set through the status API at all (everything except creation and cancellation).</summary>
    public static IReadOnlyCollection<ParcelStatusCode> Settable => Allowed.Keys;
}
