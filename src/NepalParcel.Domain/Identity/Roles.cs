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

    /// <summary>Staff who change parcel status (counter, warehouse and delivery roles; each role may make only some changes).</summary>
    public const string StatusUpdatersCsv = $"{SuperAdmin},{BranchManager},{BookingStaff},{WarehouseStaff},{DeliveryAgent}";

    /// <summary>Staff who scan parcels in and out of warehouses.</summary>
    public const string ScannersCsv = $"{SuperAdmin},{BranchManager},{WarehouseStaff}";

    public static IReadOnlyList<string> CounterStaff { get; } = [SuperAdmin, BranchManager, BookingStaff];

    public static IReadOnlyList<string> ParcelViewers { get; } = [SuperAdmin, BranchManager, BookingStaff, WarehouseStaff, Accountant];

    /// <summary>Comma-separated list for <c>[Authorize(Roles = ...)]</c>.</summary>
    public static string Any(params string[] roles) => string.Join(',', roles);
}
