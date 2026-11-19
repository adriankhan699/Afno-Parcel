using NepalParcel.Domain.Common;
using NepalParcel.Domain.Payments;
using NepalParcel.Domain.Pricing;

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

/// <summary>Everything needed to book a parcel. The price is calculated by the server and passed in as a breakdown.</summary>
public sealed record NewParcelDetails
{
    public required string TrackingNumber { get; init; }

    public required Guid SenderCustomerId { get; init; }

    public required string SenderName { get; init; }

    public required string SenderPhone { get; init; }

    public required NepalAddress SenderAddress { get; init; }

    public string? SenderAddressInstructions { get; init; }

    public required string ReceiverName { get; init; }

    public required string ReceiverPhone { get; init; }

    public required NepalAddress ReceiverAddress { get; init; }

    public string? ReceiverAddressInstructions { get; init; }

    public required Guid OriginBranchId { get; init; }

    public Guid? DestinationBranchId { get; init; }

    public required ServiceType ServiceType { get; init; }

    public required BookingChannel Channel { get; init; }

    public required string Description { get; init; }

    public required int Quantity { get; init; }

    public required decimal WeightKg { get; init; }

    public decimal? LengthCm { get; init; }

    public decimal? WidthCm { get; init; }

    public decimal? HeightCm { get; init; }

    public decimal DeclaredValue { get; init; }

    public required PaymentMethod PaymentMethod { get; init; }

    /// <summary>The server-calculated delivery charge.</summary>
    public required PriceBreakdown Price { get; init; }

    /// <summary>Goods money (NPR) to collect from the receiver on behalf of the sender; 0 if none.</summary>
    public decimal CodGoodsAmount { get; init; }

    public Guid? PricingRuleId { get; init; }

    public string? Notes { get; init; }
}

/// <summary>
/// A booked parcel. All money is in NPR (decimal, 2 places); all timestamps are UTC.
/// The aggregate owns its items and its append-only status history.
/// </summary>
public sealed class Parcel : AuditableEntity
{
    public const int NameMaxLength = 150;
    public const int NotesMaxLength = 500;
    public const int DescriptionMaxLength = 200;
    public const int InstructionsMaxLength = 300;
    public const int MaxQuantity = 100;

    private readonly List<ParcelItem> _items = [];
    private readonly List<ParcelStatusHistory> _statusHistory = [];

    private Parcel()
    {
        TrackingNumber = string.Empty;
        SenderName = string.Empty;
        SenderPhone = string.Empty;
        SenderAddress = null!;
        ReceiverName = string.Empty;
        ReceiverPhone = string.Empty;
        ReceiverAddress = null!;
        Description = string.Empty;
    }

    public string TrackingNumber { get; private set; }

    public Guid SenderCustomerId { get; private set; }

    /// <summary>The sender as written on this booking. A snapshot: it does not change if the customer later edits their profile.</summary>
    public string SenderName { get; private set; }

    public string SenderPhone { get; private set; }

    public NepalAddress SenderAddress { get; private set; }

    public string? SenderAddressInstructions { get; private set; }

    public string ReceiverName { get; private set; }

    public string ReceiverPhone { get; private set; }

    public NepalAddress ReceiverAddress { get; private set; }

    public string? ReceiverAddressInstructions { get; private set; }

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

    /// <summary>What is inside the parcel.</summary>
    public string Description { get; private set; }

    /// <summary>Number of pieces.</summary>
    public int Quantity { get; private set; }

    /// <summary>Total weight of all pieces in kilograms, as entered by the sender.</summary>
    public decimal WeightKg { get; private set; }

    public decimal? LengthCm { get; private set; }

    public decimal? WidthCm { get; private set; }

    public decimal? HeightCm { get; private set; }

    /// <summary>Weight implied by the parcel's size; 0 when no dimensions were given.</summary>
    public decimal VolumetricWeightKg { get; private set; }

    /// <summary>The weight the delivery charge was calculated on.</summary>
    public decimal ChargeableWeightKg { get; private set; }

    /// <summary>Value declared by the sender in NPR (used for insurance/claims).</summary>
    public decimal DeclaredValue { get; private set; }

    public PaymentMethod PaymentMethod { get; private set; }

    /// <summary>The pricing rule that produced the charge, kept so the price can always be explained.</summary>
    public Guid? PricingRuleId { get; private set; }

    /// <summary>Fixed charge of the weight band, in NPR.</summary>
    public decimal BaseCharge { get; private set; }

    /// <summary>Per-kilogram charge above the start of the weight band, in NPR.</summary>
    public decimal WeightCharge { get; private set; }

    /// <summary>Fee for collecting goods money on delivery, in NPR.</summary>
    public decimal CodFee { get; private set; }

    public decimal VatPercent { get; private set; }

    public decimal VatAmount { get; private set; }

    /// <summary>Total delivery charge in NPR: shipping + COD fee + VAT. Never changes after booking.</summary>
    public decimal DeliveryCharge { get; private set; }

    /// <summary>Base charge + weight charge.</summary>
    public decimal ShippingCharge => BaseCharge + WeightCharge;

    /// <summary>
    /// Amount the rider collects from the receiver on delivery in NPR: the goods amount plus, for CashOnDelivery
    /// bookings, the delivery charge. Zero when nothing is collected.
    /// </summary>
    public decimal CodAmount { get; private set; }

    public bool IsCod => CodAmount > 0;

    public string? Notes { get; private set; }

    public DateTimeOffset BookedAtUtc { get; private set; }

    public DateTimeOffset? EstimatedDeliveryAtUtc { get; private set; }

    public DateTimeOffset? DeliveredAtUtc { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyCollection<ParcelItem> Items => _items;

    public IReadOnlyCollection<ParcelStatusHistory> StatusHistory => _statusHistory;

    /// <summary>The stored price as a breakdown, e.g. for receipts.</summary>
    public PriceBreakdown Price => new(
        WeightKg, VolumetricWeightKg, ChargeableWeightKg, BaseCharge, WeightCharge, ShippingCharge, CodFee, VatPercent, VatAmount, DeliveryCharge);

    public static Parcel Book(
        NewParcelDetails details,
        DateTimeOffset bookedAtUtc,
        Guid? bookedByUserId = null,
        string? bookedByName = null)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(details.SenderAddress);
        ArgumentNullException.ThrowIfNull(details.ReceiverAddress);
        ArgumentNullException.ThrowIfNull(details.Price);

        if (!TrackingNumbers.IsValid(details.TrackingNumber))
        {
            throw new DomainException("Tracking number is not valid.");
        }

        if (!Enum.IsDefined(details.ServiceType))
        {
            throw new DomainException($"'{details.ServiceType}' is not a valid service type.");
        }

        if (!Enum.IsDefined(details.Channel))
        {
            throw new DomainException($"'{details.Channel}' is not a valid booking channel.");
        }

        if (!Enum.IsDefined(details.PaymentMethod))
        {
            throw new DomainException($"'{details.PaymentMethod}' is not a valid payment method.");
        }

        if (details.Quantity is < 1 or > MaxQuantity)
        {
            throw new DomainException($"Quantity must be between 1 and {MaxQuantity}.");
        }

        var weight = Guard.Positive(details.WeightKg, "Weight");
        var volumetric = PricingCalculator.VolumetricWeightKg(details.LengthCm, details.WidthCm, details.HeightCm);
        var price = details.Price;

        // The stored price must be internally consistent and based on this parcel's weights.
        if (price.ChargeableWeightKg < weight || price.ChargeableWeightKg < volumetric)
        {
            throw new DomainException("Chargeable weight cannot be lower than the actual or volumetric weight.");
        }

        if (price.ShippingCharge != price.BaseCharge + price.WeightCharge ||
            price.TotalCharge != price.ShippingCharge + price.CodFee + price.VatAmount)
        {
            throw new DomainException("The price breakdown does not add up.");
        }

        var goodsAmount = Guard.NonNegative(details.CodGoodsAmount, "COD amount");
        var deliveryCharge = Guard.NonNegative(price.TotalCharge, "Delivery charge");

        var parcel = new Parcel
        {
            TrackingNumber = TrackingNumbers.Normalize(details.TrackingNumber),
            SenderCustomerId = Guard.NotEmpty(details.SenderCustomerId, "Sender"),
            SenderName = Guard.NotBlank(details.SenderName, "Sender name", NameMaxLength),
            SenderPhone = Guard.NormalizePhone(details.SenderPhone, "Sender phone number"),
            SenderAddress = details.SenderAddress,
            SenderAddressInstructions = Guard.Optional(details.SenderAddressInstructions, "Sender address instructions", InstructionsMaxLength),
            ReceiverName = Guard.NotBlank(details.ReceiverName, "Receiver name", NameMaxLength),
            ReceiverPhone = Guard.NormalizePhone(details.ReceiverPhone, "Receiver phone number"),
            ReceiverAddress = details.ReceiverAddress,
            ReceiverAddressInstructions = Guard.Optional(details.ReceiverAddressInstructions, "Receiver address instructions", InstructionsMaxLength),
            OriginBranchId = Guard.NotEmpty(details.OriginBranchId, "Origin branch"),
            DestinationBranchId = details.DestinationBranchId,
            CurrentBranchId = details.OriginBranchId,
            ServiceType = details.ServiceType,
            Channel = details.Channel,
            Description = Guard.NotBlank(details.Description, "Description", DescriptionMaxLength),
            Quantity = details.Quantity,
            WeightKg = weight,
            LengthCm = details.LengthCm,
            WidthCm = details.WidthCm,
            HeightCm = details.HeightCm,
            VolumetricWeightKg = volumetric,
            ChargeableWeightKg = price.ChargeableWeightKg,
            DeclaredValue = Guard.NonNegative(details.DeclaredValue, "Declared value"),
            PaymentMethod = details.PaymentMethod,
            PricingRuleId = details.PricingRuleId,
            BaseCharge = price.BaseCharge,
            WeightCharge = price.WeightCharge,
            CodFee = price.CodFee,
            VatPercent = price.VatPercent,
            VatAmount = price.VatAmount,
            DeliveryCharge = deliveryCharge,
            // With CashOnDelivery the receiver also pays the delivery charge at the door.
            CodAmount = goodsAmount + (details.PaymentMethod == PaymentMethod.CashOnDelivery ? deliveryCharge : 0m),
            Notes = Guard.Optional(details.Notes, "Notes", NotesMaxLength),
            BookedAtUtc = bookedAtUtc,
            CurrentStatusId = (int)ParcelStatusCode.BookingCreated,
        };

        parcel._statusHistory.Add(new ParcelStatusHistory(
            parcel.Id, null, ParcelStatusCode.BookingCreated, bookedAtUtc, details.OriginBranchId, null, bookedByUserId, bookedByName, "Parcel booked"));

        return parcel;
    }

    public ParcelItem AddItem(string description, int quantity, decimal weightKg, decimal declaredValue, string? category = null, bool isFragile = false)
    {
        if (Status != ParcelStatusCode.BookingCreated)
        {
            throw new DomainException("Items can only be changed while the booking has not been picked up yet.");
        }

        var item = new ParcelItem(Id, description, category, quantity, weightKg, declaredValue, isFragile);
        _items.Add(item);
        return item;
    }

    /// <summary>Whether the parcel may be cancelled now by a customer (<paramref name="byStaff"/> false) or by branch staff.</summary>
    public bool CanBeCancelled(bool byStaff) => CancellationPolicy.CanCancel(Status, byStaff);

    /// <summary>Cancels the booking if <see cref="CancellationPolicy"/> allows it for this caller.</summary>
    public ParcelStatusHistory Cancel(string reason, bool byStaff, DateTimeOffset atUtc, Guid? cancelledByUserId, string? cancelledByName)
    {
        var trimmed = Guard.NotBlank(reason, "Cancellation reason", CancellationPolicy.ReasonMaxLength);
        if (trimmed.Length < CancellationPolicy.ReasonMinLength)
        {
            throw new DomainException($"Cancellation reason must be at least {CancellationPolicy.ReasonMinLength} characters.");
        }

        if (!CanBeCancelled(byStaff))
        {
            throw new DomainException(CancellationPolicy.Explain(Status, byStaff));
        }

        var by = byStaff ? "staff" : "customer";
        return ChangeStatus(ParcelStatusCode.Cancelled, atUtc, cancelledByUserId, cancelledByName, $"Cancelled by {by}: {trimmed}");
    }

    /// <summary>Whether the parcel has ever had a failed delivery attempt.</summary>
    public bool HasFailedDelivery => _statusHistory.Any(h => h.ToStatusId == (int)ParcelStatusCode.DeliveryFailed);

    /// <summary>
    /// The statuses this parcel may move to right now: what <see cref="ParcelStatusWorkflow"/> allows from the current
    /// status, except that a parcel can be returned only after a delivery attempt has failed.
    /// </summary>
    public IReadOnlyList<ParcelStatusCode> AllowedNextStatuses() =>
        ParcelStatusWorkflow.NextStatuses(Status)
            .Where(s => s != ParcelStatusCode.Returned || HasFailedDelivery)
            .ToList();

    /// <summary>
    /// Moves the parcel to the next status and appends a history row (previous status, new status, who, where, when,
    /// remarks). History is only ever appended to. Illegal transitions, missing remarks and missing locations are rejected
    /// and leave the parcel untouched.
    /// </summary>
    public ParcelStatusHistory ChangeStatus(
        ParcelStatusCode to,
        DateTimeOffset atUtc,
        Guid? changedByUserId,
        string? changedByName,
        string? remarks = null,
        Guid? branchId = null,
        Guid? warehouseId = null)
    {
        if (!Enum.IsDefined(to))
        {
            throw new DomainException($"'{to}' is not a valid parcel status.");
        }

        if (!ParcelStatusWorkflow.CanTransition(Status, to))
        {
            var allowed = ParcelStatusWorkflow.NextStatuses(Status);
            var hint = allowed.Count == 0
                ? "No further changes are possible."
                : $"It can move to: {string.Join(", ", allowed.Select(s => ParcelStatusCatalog.Get(s).Name))}.";
            throw new DomainException(
                $"A parcel that is '{ParcelStatusCatalog.Get(Status).Name}' cannot move to '{ParcelStatusCatalog.Get(to).Name}'. {hint}");
        }

        if (to == ParcelStatusCode.Returned && !HasFailedDelivery)
        {
            throw new DomainException("A parcel can be returned only after a delivery attempt has failed.");
        }

        if (_statusHistory.Count > 0 && atUtc < _statusHistory.Max(h => h.OccurredAtUtc))
        {
            throw new DomainException("A status change cannot be dated earlier than the previous one.");
        }

        ParcelStatusRules.Validate(to, remarks, branchId, warehouseId);

        var history = new ParcelStatusHistory(Id, Status, to, atUtc, branchId, warehouseId, changedByUserId, changedByName, remarks);
        _statusHistory.Add(history);

        CurrentStatusId = (int)to;
        if (to is ParcelStatusCode.InTransit or ParcelStatusCode.OutForDelivery)
        {
            // On a vehicle or with a rider: not at any branch or warehouse. The history row still says where it left from.
            CurrentBranchId = null;
            CurrentWarehouseId = null;
        }
        else if (branchId is not null || warehouseId is not null)
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
