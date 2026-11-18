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

    /// <summary>Comma-separated list for <c>[Authorize(Roles = ...)]</c>.</summary>
    public static string Any(params string[] roles) => string.Join(',', roles);
}
