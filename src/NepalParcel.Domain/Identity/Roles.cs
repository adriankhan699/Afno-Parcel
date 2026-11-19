namespace NepalParcel.Domain.Identity;

/// <summary>Application roles. Names are stable identifiers used in JWT claims and [Authorize] attributes.</summary>
public static class Roles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string BranchManager = "BranchManager";
    public const string BookingStaff = "BookingStaff";
    public const string WarehouseStaff = "WarehouseStaff";
    public const string DeliveryAgent = "DeliveryAgent";
    public const string Accountant = "Accountant";
    public const string Customer = "Customer";

    public static IReadOnlyList<string> All { get; } =
        [SuperAdmin, BranchManager, BookingStaff, WarehouseStaff, DeliveryAgent, Accountant, Customer];

    /// <summary>Roles that can be assigned to staff accounts (SuperAdmin is only created by bootstrap configuration).</summary>
    public static IReadOnlyList<string> AssignableStaff { get; } =
        [BranchManager, BookingStaff, WarehouseStaff, DeliveryAgent, Accountant];

    /// <summary>Staff who book parcels for walk-in customers and cancel bookings at the counter. Comma-separated for <c>[Authorize(Roles = ...)]</c>.</summary>
    public const string CounterStaffCsv = $"{SuperAdmin},{BranchManager},{BookingStaff}";

    /// <summary>Staff who may view every parcel. Comma-separated for <c>[Authorize(Roles = ...)]</c>.</summary>
    public const string ParcelViewersCsv = $"{SuperAdmin},{BranchManager},{BookingStaff},{WarehouseStaff},{Accountant}";

    /// <summary>Roles allowed to book a parcel: customers online, counter staff for walk-ins.</summary>
    public const string ParcelBookersCsv = $"{Customer},{CounterStaffCsv}";

    /// <summary>Roles that may view parcels at all: customers (their own) and parcel viewers (all).</summary>
    public const string ParcelReadersCsv = $"{Customer},{ParcelViewersCsv}";

    /// <summary>
    /// Staff who change parcel status through the status API (counter and warehouse roles; each role may make only some changes).
    /// Delivery agents are not here: they work only on parcels assigned to them, through the agent API.
    /// </summary>
    public const string StatusUpdatersCsv = $"{SuperAdmin},{BranchManager},{BookingStaff},{WarehouseStaff}";

    /// <summary>Staff who scan parcels in and out of warehouses.</summary>
    public const string ScannersCsv = $"{SuperAdmin},{BranchManager},{WarehouseStaff}";

    /// <summary>Everyone who works in the network (all roles except Customer). Comma-separated for <c>[Authorize(Roles = ...)]</c>.</summary>
    public const string StaffRolesCsv = $"{SuperAdmin},{BranchManager},{BookingStaff},{WarehouseStaff},{DeliveryAgent},{Accountant}";

    /// <summary>People who look after the money: reports, transactions and cash settlements.</summary>
    public const string FinanceCsv = $"{SuperAdmin},{BranchManager},{Accountant}";

    /// <summary>People who manage the branch and warehouse network.</summary>
    public const string NetworkAdminsCsv = $"{SuperAdmin},{BranchManager}";

    /// <summary>Roles that work in the network and can be assigned to a branch (everyone except customers).</summary>
    public static IReadOnlyList<string> Staff { get; } = [SuperAdmin, BranchManager, BookingStaff, WarehouseStaff, DeliveryAgent, Accountant];

    /// <summary>People who manage the branch and warehouse network.</summary>
    public static IReadOnlyList<string> NetworkAdmins { get; } = [SuperAdmin, BranchManager];

    public static IReadOnlyList<string> CounterStaff { get; } = [SuperAdmin, BranchManager, BookingStaff];

    public static IReadOnlyList<string> ParcelViewers { get; } = [SuperAdmin, BranchManager, BookingStaff, WarehouseStaff, Accountant];

    /// <summary>Comma-separated list for <c>[Authorize(Roles = ...)]</c>.</summary>
    public static string Any(params string[] roles) => string.Join(',', roles);
}
