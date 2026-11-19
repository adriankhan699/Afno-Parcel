using NepalParcel.Application.Common.Mapping;
using NepalParcel.Contracts.Parcels;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Payments;
using NepalParcel.Domain.Pricing;

namespace NepalParcel.Application.Parcels;

internal static class ParcelMappings
{
    private static readonly Dictionary<int, string> StatusNames =
        ParcelStatusCatalog.All.ToDictionary(e => (int)e.Code, e => e.Name);

    public static string StatusName(int statusId) =>
        StatusNames.TryGetValue(statusId, out var name) ? name : statusId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static PriceBreakdownDto ToDto(this PriceBreakdown b) =>
        new(b.ActualWeightKg, b.VolumetricWeightKg, b.ChargeableWeightKg, b.BaseCharge, b.WeightCharge, b.ShippingCharge,
            b.CodFee, b.VatPercent, b.VatAmount, b.TotalCharge);

    public static BranchSummaryDto ToSummary(this Branch b) =>
        new(b.Id, b.Code, b.Name, b.PhoneNumber, b.Address.ToDto());

    /// <param name="locations">Names of the branches and warehouses that appear in the history.</param>
    /// <param name="showStaff">Whether to include which staff member made each change (staff only, never customers).</param>
    /// <param name="allowedNextStatuses">The statuses the caller may move the parcel to now.</param>
    public static ParcelResponse ToResponse(
        this Parcel p,
        Payment? payment,
        Branch origin,
        Branch? destination,
        bool canCancel,
        IReadOnlyDictionary<Guid, string> locations,
        bool showStaff,
        IReadOnlyList<string> allowedNextStatuses) =>
        new(
            p.Id,
            p.TrackingNumber,
            p.Status.ToString(),
            StatusName(p.CurrentStatusId),
            p.ServiceType.ToString(),
            p.Channel.ToString(),
            new BookingPartyDto(p.SenderName, p.SenderPhone, p.SenderAddress.ToDto(), p.SenderAddressInstructions),
            new BookingPartyDto(p.ReceiverName, p.ReceiverPhone, p.ReceiverAddress.ToDto(), p.ReceiverAddressInstructions),
            origin.ToSummary(),
            destination?.ToSummary(),
            p.Description,
            p.Quantity,
            p.WeightKg,
            p.LengthCm,
            p.WidthCm,
            p.HeightCm,
            p.DeclaredValue,
            p.PaymentMethod.ToString(),
            payment?.Status.ToString(),
            payment?.Reference,
            p.Price.ToDto(),
            p.CodAmount,
            p.Notes,
            p.BookedAtUtc,
            p.EstimatedDeliveryAtUtc,
            p.DeliveredAtUtc,
            canCancel,
            p.StatusHistory
                .OrderBy(h => h.OccurredAtUtc)
                .Select(h => new ParcelStatusEventDto(
                    ((ParcelStatusCode)h.ToStatusId).ToString(),
                    StatusName(h.ToStatusId),
                    h.OccurredAtUtc,
                    h.Remarks,
                    h.FromStatusId is { } from ? ((ParcelStatusCode)from).ToString() : null,
                    LocationName(h, locations),
                    showStaff ? h.ChangedByName : null))
                .ToList(),
            allowedNextStatuses);

    private static string? LocationName(ParcelStatusHistory h, IReadOnlyDictionary<Guid, string> locations) =>
        h.WarehouseId is { } w && locations.TryGetValue(w, out var warehouse) ? warehouse
        : h.BranchId is { } b && locations.TryGetValue(b, out var branch) ? branch
        : null;

    public static ParcelSummaryResponse ToResponse(this ParcelListItem i) =>
        new(
            i.Id,
            i.TrackingNumber,
            ((ParcelStatusCode)i.StatusId).ToString(),
            StatusName(i.StatusId),
            i.ServiceType.ToString(),
            i.Channel.ToString(),
            i.PaymentMethod.ToString(),
            i.PaymentStatus?.ToString(),
            i.SenderName,
            i.ReceiverName,
            i.ReceiverDistrict,
            i.ReceiverMunicipality,
            i.OriginBranchName,
            i.Quantity,
            i.WeightKg,
            i.DeliveryCharge,
            i.CodAmount,
            i.BookedAtUtc);

    public static ServiceAreaResponse ToResponse(this ServiceArea a) =>
        new(a.Id, a.Code, a.Name, a.Province.ToString(), a.District, a.Municipality, a.IsServiceable, a.EstimatedDeliveryDays, a.ServingBranchId);

    public static PricingRuleResponse ToResponse(this PricingRule r, IReadOnlyDictionary<Guid, string> areaNames) =>
        new(
            r.Id,
            r.Name,
            r.ServiceType.ToString(),
            r.FromServiceAreaId,
            r.FromServiceAreaId is { } from && areaNames.TryGetValue(from, out var fromName) ? fromName : null,
            r.ToServiceAreaId,
            r.ToServiceAreaId is { } to && areaNames.TryGetValue(to, out var toName) ? toName : null,
            r.MinWeightKg,
            r.MaxWeightKg,
            r.BasePrice,
            r.PricePerKg,
            r.CodFeePercent,
            r.Priority,
            r.IsActive,
            r.EffectiveFromUtc,
            r.EffectiveToUtc);
}
