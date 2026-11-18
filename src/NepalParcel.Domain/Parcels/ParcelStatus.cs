namespace NepalParcel.Domain.Parcels;

/// <summary>Stable ids of the seeded parcel statuses (also the primary keys of the ParcelStatuses lookup table).</summary>
public enum ParcelStatusCode
{
    Booked = 1,
    PickedUp = 2,
    ReceivedAtBranch = 3,
    InTransit = 4,
    ReceivedAtWarehouse = 5,
    OutForDelivery = 6,
    Delivered = 7,
    DeliveryFailed = 8,
    ReturnToSender = 9,
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

/// <summary>The initial data for <see cref="ParcelStatus"/>.</summary>
public static class ParcelStatusCatalog
{
    public sealed record Entry(ParcelStatusCode Code, string Name, string Description, bool IsTerminal);

    public static IReadOnlyList<Entry> All { get; } =
    [
        new(ParcelStatusCode.Booked, "Booked", "Booking confirmed; waiting to be handed over.", false),
        new(ParcelStatusCode.PickedUp, "Picked up", "Collected from the sender.", false),
        new(ParcelStatusCode.ReceivedAtBranch, "Received at branch", "Received and checked at an Afno Parcel branch.", false),
        new(ParcelStatusCode.InTransit, "In transit", "On its way between branches or hubs.", false),
        new(ParcelStatusCode.ReceivedAtWarehouse, "Received at warehouse", "Stored at a warehouse or sorting hub.", false),
        new(ParcelStatusCode.OutForDelivery, "Out for delivery", "With a delivery agent for the final leg.", false),
        new(ParcelStatusCode.Delivered, "Delivered", "Handed over to the recipient.", true),
        new(ParcelStatusCode.DeliveryFailed, "Delivery attempt failed", "Recipient unavailable or address problem.", false),
        new(ParcelStatusCode.ReturnToSender, "Return to sender", "Being sent back to the sender.", false),
        new(ParcelStatusCode.Returned, "Returned", "Returned to the sender.", true),
        new(ParcelStatusCode.Cancelled, "Cancelled", "Booking cancelled before dispatch.", true),
    ];
}

/// <summary>Defines which status changes are allowed.</summary>
public static class ParcelStatusWorkflow
{
    private static readonly Dictionary<ParcelStatusCode, ParcelStatusCode[]> Allowed = new()
    {
        [ParcelStatusCode.Booked] = [ParcelStatusCode.PickedUp, ParcelStatusCode.ReceivedAtBranch, ParcelStatusCode.Cancelled],
        [ParcelStatusCode.PickedUp] = [ParcelStatusCode.ReceivedAtBranch, ParcelStatusCode.Cancelled],
        [ParcelStatusCode.ReceivedAtBranch] =
            [ParcelStatusCode.InTransit, ParcelStatusCode.ReceivedAtWarehouse, ParcelStatusCode.OutForDelivery, ParcelStatusCode.Cancelled],
        [ParcelStatusCode.InTransit] =
            [ParcelStatusCode.ReceivedAtBranch, ParcelStatusCode.ReceivedAtWarehouse, ParcelStatusCode.OutForDelivery],
        [ParcelStatusCode.ReceivedAtWarehouse] = [ParcelStatusCode.InTransit, ParcelStatusCode.OutForDelivery],
        [ParcelStatusCode.OutForDelivery] = [ParcelStatusCode.Delivered, ParcelStatusCode.DeliveryFailed],
        [ParcelStatusCode.DeliveryFailed] =
            [ParcelStatusCode.OutForDelivery, ParcelStatusCode.ReceivedAtBranch, ParcelStatusCode.ReturnToSender],
        [ParcelStatusCode.ReturnToSender] = [ParcelStatusCode.InTransit, ParcelStatusCode.ReceivedAtBranch, ParcelStatusCode.Returned],
    };

    public static IReadOnlyCollection<ParcelStatusCode> NextStatuses(ParcelStatusCode from) =>
        Allowed.TryGetValue(from, out var next) ? next : [];

    public static bool CanTransition(ParcelStatusCode from, ParcelStatusCode to) => NextStatuses(from).Contains(to);
}
