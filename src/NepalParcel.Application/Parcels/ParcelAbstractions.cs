using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Parcels;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Payments;
using NepalParcel.Domain.Pricing;

namespace NepalParcel.Application.Parcels;

/// <summary>Search criteria for the parcel list. Dates are already converted to UTC instants.</summary>
/// <param name="SenderCustomerId">When set, only this customer's parcels (used for customers; staff leave it null).</param>
/// <param name="Search">Tracking number prefix, sender/receiver name or phone number.</param>
/// <param name="BookedFromUtc">Inclusive start of the booking time range.</param>
/// <param name="BookedBeforeUtc">Exclusive end of the booking time range.</param>
public sealed record ParcelFilter(
    Guid? SenderCustomerId = null,
    string? Search = null,
    ParcelStatusCode? Status = null,
    ServiceType? ServiceType = null,
    PaymentMethod? PaymentMethod = null,
    BookingChannel? Channel = null,
    bool? IsCod = null,
    Guid? OriginBranchId = null,
    DateTimeOffset? BookedFromUtc = null,
    DateTimeOffset? BookedBeforeUtc = null);

/// <summary>A row of the parcel list, projected by the database so the list never loads whole aggregates.</summary>
public sealed record ParcelListItem(
    Guid Id,
    string TrackingNumber,
    int StatusId,
    ServiceType ServiceType,
    BookingChannel Channel,
    PaymentMethod PaymentMethod,
    PaymentStatus? PaymentStatus,
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

public interface IParcelRepository
{
    void Add(Parcel parcel);

    /// <summary>Loads the parcel with its status history (tracked).</summary>
    Task<Parcel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Loads the parcel by tracking number (already normalised) with its status history (tracked).</summary>
    Task<Parcel?> GetByTrackingNumberAsync(string trackingNumber, CancellationToken cancellationToken = default);

    Task<bool> TrackingNumberExistsAsync(string trackingNumber, CancellationToken cancellationToken = default);

    /// <summary>Newest bookings first.</summary>
    Task<(IReadOnlyList<ParcelListItem> Items, int TotalCount)> SearchAsync(
        ParcelFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}

public interface IPaymentRepository
{
    void Add(Payment payment);

    /// <summary>The most recent payment for the parcel, tracked so it can be updated.</summary>
    Task<Payment?> GetLatestByParcelIdAsync(Guid parcelId, CancellationToken cancellationToken = default);
}

public interface IPricingRepository
{
    Task<IReadOnlyList<ServiceArea>> GetServiceAreasAsync(CancellationToken cancellationToken = default);

    /// <summary>The district-wide area and any municipality areas of one district.</summary>
    Task<IReadOnlyList<ServiceArea>> GetServiceAreasInDistrictAsync(Province province, string district, CancellationToken cancellationToken = default);

    Task<bool> ServiceAreaExistsAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Rules that are active and in date at <paramref name="atUtc"/> for the service type.</summary>
    Task<IReadOnlyList<PricingRule>> GetEffectiveRulesAsync(ServiceType serviceType, DateTimeOffset atUtc, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<PricingRule> Items, int TotalCount)> GetRulesPageAsync(int page, int pageSize, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, string>> GetServiceAreaNamesAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default);

    Task<PricingRule?> GetRuleByIdAsync(Guid id, CancellationToken cancellationToken = default);

    void AddRule(PricingRule rule);
}

public interface IParcelService
{
    Task<ParcelResponse> BookAsync(BookParcelRequest request, CancellationToken cancellationToken = default);

    Task<PagedResponse<ParcelSummaryResponse>> SearchAsync(ParcelSearchQuery query, CancellationToken cancellationToken = default);

    Task<ParcelResponse> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ParcelResponse> GetByTrackingNumberAsync(string trackingNumber, CancellationToken cancellationToken = default);

    Task<ParcelResponse> CancelAsync(Guid id, CancelParcelRequest request, CancellationToken cancellationToken = default);

    Task<ParcelResponse> ConfirmPaymentAsync(Guid id, ConfirmPaymentRequest request, CancellationToken cancellationToken = default);

    Task<ReceiptResponse> GetReceiptAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IPricingService
{
    Task<QuoteResponse> QuoteAsync(QuoteRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ServiceAreaResponse>> GetServiceAreasAsync(CancellationToken cancellationToken = default);

    Task<PagedResponse<PricingRuleResponse>> GetRulesAsync(PageQuery query, CancellationToken cancellationToken = default);

    Task<PricingRuleResponse> CreateRuleAsync(CreatePricingRuleRequest request, CancellationToken cancellationToken = default);

    Task DeactivateRuleAsync(Guid id, CancellationToken cancellationToken = default);
}
