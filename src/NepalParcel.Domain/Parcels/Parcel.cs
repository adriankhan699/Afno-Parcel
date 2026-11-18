using NepalParcel.Domain.Common;

namespace NepalParcel.Domain.Parcels;

public enum ServiceType
{
    Standard = 1,
    Express = 2,
    SameDay = 3,
}

public enum BookingChannel
{
    Online = 1,
    Counter = 2,
}

/// <summary>
/// A booked parcel. All money is in NPR (decimal, 2 places); all timestamps are UTC.
/// The aggregate owns its items and its append-only status history.
/// </summary>
public sealed class Parcel : AuditableEntity
{
    public const int NameMaxLength = 150;
    public const int NotesMaxLength = 500;

    private readonly List<ParcelItem> _items = [];
    private readonly List<ParcelStatusHistory> _statusHistory = [];

    private Parcel()
    {
        TrackingNumber = string.Empty;
        ReceiverName = string.Empty;
        ReceiverPhone = string.Empty;
        ReceiverAddress = null!;
    }

    public string TrackingNumber { get; private set; }

    public Guid SenderCustomerId { get; private set; }

    public string ReceiverName { get; private set; }

    public string ReceiverPhone { get; private set; }

    public NepalAddress ReceiverAddress { get; private set; }

    public Guid OriginBranchId { get; private set; }

    public Guid? DestinationBranchId { get; private set; }

    /// <summary>Branch currently holding the parcel, if any.</summary>
    public Guid? CurrentBranchId { get; private set; }

    /// <summary>Warehouse currently holding the parcel, if any.</summary>
    public Guid? CurrentWarehouseId { get; private set; }

    public int CurrentStatusId { get; private set; }

    public ParcelStatusCode Status => (ParcelStatusCode)CurrentStatusId;

    public ServiceType ServiceType { get; private set; }

    public BookingChannel Channel { get; private set; }

    public decimal WeightKg { get; private set; }

    /// <summary>Value declared by the sender in NPR (used for insurance/claims).</summary>
    public decimal DeclaredValue { get; private set; }

    /// <summary>Delivery charge in NPR.</summary>
    public decimal DeliveryCharge { get; private set; }

    /// <summary>Amount to collect from the receiver on delivery in NPR; zero when the parcel is not cash-on-delivery.</summary>
    public decimal CodAmount { get; private set; }

    public bool IsCod => CodAmount > 0;

    public string? Notes { get; private set; }

    public DateTimeOffset BookedAtUtc { get; private set; }

    public DateTimeOffset? EstimatedDeliveryAtUtc { get; private set; }

    public DateTimeOffset? DeliveredAtUtc { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyCollection<ParcelItem> Items => _items;

    public IReadOnlyCollection<ParcelStatusHistory> StatusHistory => _statusHistory;

    public static Parcel Book(
        string trackingNumber,
        Guid senderCustomerId,
        string receiverName,
        string receiverPhone,
        NepalAddress receiverAddress,
        Guid originBranchId,
        Guid? destinationBranchId,
        ServiceType serviceType,
        BookingChannel channel,
        decimal weightKg,
        decimal declaredValue,
        decimal deliveryCharge,
        decimal codAmount,
        DateTimeOffset bookedAtUtc,
        Guid? bookedByUserId = null,
        string? bookedByName = null,
        string? notes = null)
    {
        ArgumentNullException.ThrowIfNull(receiverAddress);
        if (!TrackingNumbers.IsValid(trackingNumber))
        {
            throw new DomainException("Tracking number is not valid.");
        }

        if (!Enum.IsDefined(serviceType))
        {
            throw new DomainException($"'{serviceType}' is not a valid service type.");
        }

        if (!Enum.IsDefined(channel))
        {
            throw new DomainException($"'{channel}' is not a valid booking channel.");
        }

        var parcel = new Parcel
        {
            TrackingNumber = TrackingNumbers.Normalize(trackingNumber),
            SenderCustomerId = Guard.NotEmpty(senderCustomerId, "Sender"),
            ReceiverName = Guard.NotBlank(receiverName, "Receiver name", NameMaxLength),
            ReceiverPhone = Guard.NormalizePhone(receiverPhone, "Receiver phone number"),
            ReceiverAddress = receiverAddress,
            OriginBranchId = Guard.NotEmpty(originBranchId, "Origin branch"),
            DestinationBranchId = destinationBranchId,
            CurrentBranchId = originBranchId,
            ServiceType = serviceType,
            Channel = channel,
            WeightKg = Guard.Positive(weightKg, "Weight"),
            DeclaredValue = Guard.NonNegative(declaredValue, "Declared value"),
            DeliveryCharge = Guard.NonNegative(deliveryCharge, "Delivery charge"),
            CodAmount = Guard.NonNegative(codAmount, "COD amount"),
            Notes = Guard.Optional(notes, "Notes", NotesMaxLength),
            BookedAtUtc = bookedAtUtc,
            CurrentStatusId = (int)ParcelStatusCode.Booked,
        };

        parcel._statusHistory.Add(new ParcelStatusHistory(
            parcel.Id, null, ParcelStatusCode.Booked, bookedAtUtc, originBranchId, null, bookedByUserId, bookedByName, "Parcel booked"));

        return parcel;
    }

    public ParcelItem AddItem(string description, int quantity, decimal weightKg, decimal declaredValue, string? category = null, bool isFragile = false)
    {
        if (Status != ParcelStatusCode.Booked)
        {
            throw new DomainException("Items can only be changed while the parcel is still in Booked status.");
        }

        var item = new ParcelItem(Id, description, category, quantity, weightKg, declaredValue, isFragile);
        _items.Add(item);
        return item;
    }

    /// <summary>Moves the parcel to the next status and appends a history row. Illegal transitions are rejected.</summary>
    public ParcelStatusHistory ChangeStatus(
        ParcelStatusCode to,
        DateTimeOffset atUtc,
        Guid? changedByUserId,
        string? changedByName,
        string? remarks = null,
        Guid? branchId = null,
        Guid? warehouseId = null)
    {
        if (!ParcelStatusWorkflow.CanTransition(Status, to))
        {
            throw new DomainException($"A parcel in status '{Status}' cannot move to '{to}'.");
        }

        var history = new ParcelStatusHistory(Id, Status, to, atUtc, branchId, warehouseId, changedByUserId, changedByName, remarks);
        _statusHistory.Add(history);

        CurrentStatusId = (int)to;
        if (branchId is not null || warehouseId is not null)
        {
            CurrentBranchId = branchId;
            CurrentWarehouseId = warehouseId;
        }

        if (to == ParcelStatusCode.Delivered)
        {
            DeliveredAtUtc = atUtc;
        }

        return history;
    }

    public void SetEstimatedDelivery(DateTimeOffset estimatedUtc) => EstimatedDeliveryAtUtc = estimatedUtc;

}
