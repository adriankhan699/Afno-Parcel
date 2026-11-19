using NepalParcel.Contracts.Common;

namespace NepalParcel.Contracts.Parcels;

/// <summary>The sender or the receiver of a parcel.</summary>
/// <param name="FullName">Full name (max 150 characters).</param>
/// <param name="PhoneNumber">Nepali mobile (98XXXXXXXX) or landline (01-XXXXXXX) number.</param>
/// <param name="Address">Province, district, municipality, ward and street or locality.</param>
/// <param name="AddressInstructions">Optional help for the rider, e.g. "Blue gate, call before arriving" (max 300 characters).</param>
public sealed record BookingPartyDto(
    string FullName,
    string PhoneNumber,
    AddressDto Address,
    string? AddressInstructions = null);

/// <summary>
/// Books a parcel. There is deliberately no price in this request: the delivery charge is always calculated by the
/// server from the pricing rules, and any price a client sends is ignored.
/// </summary>
/// <param name="Sender">Who is sending the parcel.</param>
/// <param name="Receiver">Who receives the parcel.</param>
/// <param name="OriginBranchId">The branch where the parcel is handed over. Pricing starts from this branch's area.</param>
/// <param name="Description">What is inside (max 200 characters).</param>
/// <param name="Quantity">Number of pieces (1–100).</param>
/// <param name="WeightKg">Total weight of all pieces in kilograms.</param>
/// <param name="ServiceType">Standard, Express or SameDay.</param>
/// <param name="PaymentMethod">Cash, Esewa, Khalti, ConnectIps, BankTransfer (prepaid) or CashOnDelivery (the receiver pays the delivery charge on delivery).</param>
/// <param name="LengthCm">Optional length in cm. Give all three dimensions or none.</param>
/// <param name="WidthCm">Optional width in cm.</param>
/// <param name="HeightCm">Optional height in cm.</param>
/// <param name="DeclaredValue">Optional value of the contents in NPR.</param>
/// <param name="CodAmount">Optional goods amount in NPR to collect from the receiver on behalf of the sender (a COD fee applies).</param>
/// <param name="PaymentReference">Optional payment reference (e.g. wallet transaction id) recorded by counter staff for prepaid bookings.</param>
/// <param name="Notes">Optional internal notes (max 500 characters).</param>
/// <param name="NotifyBySms">Whether the sender agrees to parcel updates by SMS. Leave out to keep what is already recorded (off for a new customer).</param>
/// <param name="NotifyByWhatsApp">Whether the sender agrees to parcel updates on WhatsApp. Leave out to keep what is already recorded.</param>
public sealed record BookParcelRequest(
    BookingPartyDto Sender,
    BookingPartyDto Receiver,
    Guid OriginBranchId,
    string Description,
    int Quantity,
    decimal WeightKg,
    string ServiceType,
    string PaymentMethod,
    decimal? LengthCm = null,
    decimal? WidthCm = null,
    decimal? HeightCm = null,
    decimal? DeclaredValue = null,
    decimal? CodAmount = null,
    string? PaymentReference = null,
    string? Notes = null,
    bool? NotifyBySms = null,
    bool? NotifyByWhatsApp = null);

/// <summary>Asks for the delivery charge of a parcel without booking it.</summary>
/// <param name="OriginBranchId">Branch where the parcel will be handed over.</param>
/// <param name="Destination">Where the parcel is going.</param>
/// <param name="ServiceType">Standard, Express or SameDay.</param>
/// <param name="WeightKg">Total weight in kilograms.</param>
/// <param name="LengthCm">Optional length in cm. Give all three dimensions or none.</param>
/// <param name="WidthCm">Optional width in cm.</param>
/// <param name="HeightCm">Optional height in cm.</param>
/// <param name="CodAmount">Optional goods amount in NPR to collect on delivery.</param>
public sealed record QuoteRequest(
    Guid OriginBranchId,
    AddressDto Destination,
    string ServiceType,
    decimal WeightKg,
    decimal? LengthCm = null,
    decimal? WidthCm = null,
    decimal? HeightCm = null,
    decimal? CodAmount = null);

/// <summary>How a delivery charge was calculated. All amounts are NPR.</summary>
/// <param name="ActualWeightKg">Weight entered by the sender.</param>
/// <param name="VolumetricWeightKg">Weight implied by the size (length × width × height ÷ 5000); 0 when no dimensions were given.</param>
/// <param name="ChargeableWeightKg">The larger of the two, rounded up to the next half kilogram. This is the weight priced.</param>
/// <param name="BaseCharge">Fixed charge of the weight band.</param>
/// <param name="WeightCharge">Extra charge per kilogram above the start of the weight band.</param>
/// <param name="ShippingCharge">Base charge + weight charge.</param>
/// <param name="CodFee">Fee for collecting goods money on delivery; 0 when there is no COD goods amount.</param>
/// <param name="VatPercent">VAT rate applied.</param>
/// <param name="VatAmount">VAT on shipping charge + COD fee.</param>
/// <param name="TotalCharge">Shipping charge + COD fee + VAT: the delivery charge to pay.</param>
public sealed record PriceBreakdownDto(
    decimal ActualWeightKg,
    decimal VolumetricWeightKg,
    decimal ChargeableWeightKg,
    decimal BaseCharge,
    decimal WeightCharge,
    decimal ShippingCharge,
    decimal CodFee,
    decimal VatPercent,
    decimal VatAmount,
    decimal TotalCharge);

/// <summary>The price and delivery estimate for a route.</summary>
/// <param name="ServiceType">Standard, Express or SameDay.</param>
/// <param name="OriginArea">Name of the area the parcel starts from; null when the origin branch's area is not configured.</param>
/// <param name="DestinationArea">Name of the destination service area.</param>
/// <param name="EstimatedDeliveryDays">Typical delivery time in days (0 for same-day).</param>
/// <param name="EstimatedDeliveryAtUtc">Estimated delivery time if booked now (UTC).</param>
/// <param name="RuleName">Name of the pricing rule that was applied.</param>
/// <param name="Currency">Always NPR.</param>
/// <param name="Breakdown">Itemised charge.</param>
public sealed record QuoteResponse(
    string ServiceType,
    string? OriginArea,
    string DestinationArea,
    int EstimatedDeliveryDays,
    DateTimeOffset EstimatedDeliveryAtUtc,
    string RuleName,
    string Currency,
    PriceBreakdownDto Breakdown);

/// <summary>A branch as shown on a booking.</summary>
public sealed record BranchSummaryDto(Guid Id, string Code, string Name, string PhoneNumber, AddressDto Address);

/// <summary>One step of a parcel's journey. History is append-only: rows are never edited or removed.</summary>
/// <param name="Status">Status code, e.g. BookingCreated.</param>
/// <param name="StatusName">Display name, e.g. "Picked up".</param>
/// <param name="OccurredAtUtc">When it happened (UTC; display in Nepal time).</param>
/// <param name="Remarks">Optional note, e.g. the cancellation reason.</param>
/// <param name="FromStatus">The status before this step; null for the first step.</param>
/// <param name="Location">Name of the branch or warehouse where it was recorded, if any.</param>
/// <param name="ChangedBy">Name of the staff member who made the change. Only sent to staff, never to customers.</param>
public sealed record ParcelStatusEventDto(
    string Status,
    string StatusName,
    DateTimeOffset OccurredAtUtc,
    string? Remarks,
    string? FromStatus = null,
    string? Location = null,
    string? ChangedBy = null);

/// <summary>One row of the parcel list.</summary>
public sealed record ParcelSummaryResponse(
    Guid Id,
    string TrackingNumber,
    string Status,
    string StatusName,
    string ServiceType,
    string Channel,
    string PaymentMethod,
    string? PaymentStatus,
    string SenderName,
    string ReceiverName,
    string ReceiverDistrict,
    string ReceiverMunicipality,
    string OriginBranchName,
    int Quantity,
    decimal WeightKg,
    decimal DeliveryCharge,
    decimal CodAmount,
    DateTimeOffset BookedAtUtc);

/// <summary>
/// A booked parcel with everything the sender, or branch staff, need to see.
/// <c>CodAmount</c> is what the delivery rider collects from the receiver (the goods amount plus, for CashOnDelivery,
/// the delivery charge); 0 when nothing is collected. <c>CanCancel</c> says whether the signed-in caller may cancel now.
/// <c>AllowedNextStatuses</c> lists the statuses the signed-in caller may move the parcel to right now through the
/// status API (empty for customers and for finished parcels).
/// </summary>
public sealed record ParcelResponse(
    Guid Id,
    string TrackingNumber,
    string Status,
    string StatusName,
    string ServiceType,
    string Channel,
    BookingPartyDto Sender,
    BookingPartyDto Receiver,
    BranchSummaryDto OriginBranch,
    BranchSummaryDto? DestinationBranch,
    string Description,
    int Quantity,
    decimal WeightKg,
    decimal? LengthCm,
    decimal? WidthCm,
    decimal? HeightCm,
    decimal DeclaredValue,
    string PaymentMethod,
    string? PaymentStatus,
    string? PaymentReference,
    PriceBreakdownDto Price,
    decimal CodAmount,
    string? Notes,
    DateTimeOffset BookedAtUtc,
    DateTimeOffset? EstimatedDeliveryAtUtc,
    DateTimeOffset? DeliveredAtUtc,
    bool CanCancel,
    IReadOnlyList<ParcelStatusEventDto> History,
    IReadOnlyList<string> AllowedNextStatuses);

/// <summary>Filters for the parcel list. Customers only ever see their own parcels.</summary>
public sealed record ParcelSearchQuery
{
    /// <summary>1-based page number.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Items per page (1–100).</summary>
    public int PageSize { get; init; } = PageQuery.DefaultPageSize;

    /// <summary>Tracking number (or the start of one), sender or receiver name, or a phone number.</summary>
    public string? Search { get; init; }

    /// <summary>Status code, e.g. Booked or InTransit.</summary>
    public string? Status { get; init; }

    public string? ServiceType { get; init; }

    public string? PaymentMethod { get; init; }

    /// <summary>Online or Counter.</summary>
    public string? Channel { get; init; }

    /// <summary>true = only parcels with money to collect on delivery; false = only parcels without.</summary>
    public bool? IsCod { get; init; }

    public Guid? OriginBranchId { get; init; }

    /// <summary>First booking day (inclusive), as a Nepal-time calendar date, e.g. 2026-11-18.</summary>
    public DateOnly? BookedFrom { get; init; }

    /// <summary>Last booking day (inclusive), as a Nepal-time calendar date.</summary>
    public DateOnly? BookedTo { get; init; }
}

/// <summary>Cancels a booking.</summary>
/// <param name="Reason">Why the booking is cancelled (5–300 characters).</param>
public sealed record CancelParcelRequest(string Reason);

/// <summary>Records that the sender has paid a pending prepaid delivery charge (counter staff only).</summary>
/// <param name="Reference">Optional receipt number or wallet transaction id (max 100 characters).</param>
public sealed record ConfirmPaymentRequest(string? Reference = null);

/// <summary>A printable booking receipt.</summary>
/// <param name="ReceiptNumber">Receipt number (derived from the tracking number).</param>
/// <param name="CompanyName">Company name to print in the header.</param>
/// <param name="CompanyPhone">Optional support phone number.</param>
/// <param name="CompanyTaxNumber">Optional PAN/VAT number to print.</param>
/// <param name="Parcel">The booking.</param>
public sealed record ReceiptResponse(
    string ReceiptNumber,
    string CompanyName,
    string? CompanyPhone,
    string? CompanyTaxNumber,
    ParcelResponse Parcel);

/// <summary>A place we deliver to.</summary>
public sealed record ServiceAreaResponse(
    Guid Id,
    string Code,
    string Name,
    string Province,
    string District,
    string? Municipality,
    bool IsServiceable,
    int EstimatedDeliveryDays,
    Guid? ServingBranchId);

/// <summary>A delivery price rule (admin view).</summary>
public sealed record PricingRuleResponse(
    Guid Id,
    string Name,
    string ServiceType,
    Guid? FromServiceAreaId,
    string? FromServiceAreaName,
    Guid? ToServiceAreaId,
    string? ToServiceAreaName,
    decimal MinWeightKg,
    decimal MaxWeightKg,
    decimal BasePrice,
    decimal PricePerKg,
    decimal CodFeePercent,
    int Priority,
    bool IsActive,
    DateTimeOffset EffectiveFromUtc,
    DateTimeOffset? EffectiveToUtc);

/// <summary>Adds a price rule. To change a price, deactivate the old rule and add a new one.</summary>
/// <param name="Name">Display name (max 150 characters).</param>
/// <param name="ServiceType">Standard, Express or SameDay.</param>
/// <param name="MinWeightKg">Band start (exclusive): the band covers weights above this.</param>
/// <param name="MaxWeightKg">Band end (inclusive).</param>
/// <param name="BasePrice">Flat NPR charge for the band.</param>
/// <param name="PricePerKg">Extra NPR for each kilogram above <paramref name="MinWeightKg"/>.</param>
/// <param name="CodFeePercent">COD fee as a percentage (0–100) of the goods amount collected.</param>
/// <param name="Priority">Higher wins when two rules are equally specific.</param>
/// <param name="FromServiceAreaId">Origin area, or null for any origin.</param>
/// <param name="ToServiceAreaId">Destination area, or null for any destination.</param>
/// <param name="EffectiveFromUtc">Start time; defaults to now.</param>
/// <param name="EffectiveToUtc">Optional end time.</param>
public sealed record CreatePricingRuleRequest(
    string Name,
    string ServiceType,
    decimal MinWeightKg,
    decimal MaxWeightKg,
    decimal BasePrice,
    decimal PricePerKg,
    decimal CodFeePercent,
    int Priority = 0,
    Guid? FromServiceAreaId = null,
    Guid? ToServiceAreaId = null,
    DateTimeOffset? EffectiveFromUtc = null,
    DateTimeOffset? EffectiveToUtc = null);

/// <summary>
/// Everything needed to print a parcel label: the tracking number as a Code 128 barcode and as a QR code (both SVG images as
/// <c>data:</c> addresses, ready for an <c>img</c> tag), and a few facts for the people handling it. Contains no phone numbers.
/// </summary>
public sealed record ParcelLabelResponse(
    string TrackingNumber,
    string BarcodeDataUri,
    string QrDataUri,
    string QrContent,
    string ServiceType,
    int Quantity,
    decimal WeightKg,
    string OriginBranch,
    string DestinationArea,
    string ReceiverName,
    string ReceiverArea,
    decimal CodAmount,
    DateTimeOffset BookedAtUtc);
