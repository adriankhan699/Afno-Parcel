using System.ComponentModel.DataAnnotations;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Tracking;
using NepalParcel.Contracts.Warehouses;
using NepalParcel.Domain.Notifications;
using NepalParcel.Domain.Tracking;
using NepalParcel.Domain.Warehouses;

namespace NepalParcel.Application.Tracking;

/// <summary>Tracking and one-time-code settings (configuration section <c>Tracking</c>). Not secrets.</summary>
public sealed class TrackingOptions
{
    public const string SectionName = "Tracking";

    /// <summary>How long a one-time code works.</summary>
    [Range(1, 60)]
    public int OtpLifetimeMinutes { get; set; } = 10;

    /// <summary>Wrong guesses allowed per code before it is locked.</summary>
    [Range(1, 20)]
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>Codes that can be requested for one parcel per hour; more requests are silently ignored.</summary>
    [Range(1, 50)]
    public int MaxRequestsPerHour { get; set; } = 3;

    /// <summary>How long the access token issued after a correct code works.</summary>
    [Range(1, 120)]
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>
    /// Writes each one-time code to the log so developers can sign in without an SMS provider.
    /// <b>Never enable this outside local development</b>: anyone who can read the logs could track parcels.
    /// </summary>
    public bool LogCodes { get; set; }
}

/// <summary>Hashing and signing for tracking codes and access tokens, backed by a server secret (implemented in the API).</summary>
public interface ITrackingSecurity
{
    /// <summary>A keyed hash of the code, bound to the parcel, so the database never holds a usable code.</summary>
    string HashOtp(Guid parcelId, string code);

    /// <summary>Compares in constant time.</summary>
    bool VerifyOtp(Guid parcelId, string code, string expectedHash);

    /// <summary>A signed token that proves the receiver verified, valid for exactly one tracking number until <paramref name="expiresAtUtc"/>.</summary>
    string CreateAccessToken(string trackingNumber, DateTimeOffset expiresAtUtc);

    bool ValidateAccessToken(string token, string trackingNumber, DateTimeOffset nowUtc);
}

public interface ITrackingOtpRepository
{
    void Add(TrackingOtp otp);

    /// <summary>The newest code issued for the parcel (tracked so attempts can be recorded), or null.</summary>
    Task<TrackingOtp?> GetLatestAsync(Guid parcelId, CancellationToken cancellationToken = default);

    Task<int> CountSinceAsync(Guid parcelId, DateTimeOffset sinceUtc, CancellationToken cancellationToken = default);
}

public interface INotificationOutboxRepository
{
    void Add(NotificationOutboxMessage message);
}

public interface IWarehouseRepository
{
    Task<Warehouse?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, Warehouse>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);

    Task<bool> CodeExistsAsync(string code, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<Warehouse> Items, int TotalCount)> GetPageAsync(int page, int pageSize, CancellationToken cancellationToken = default);

    void Add(Warehouse warehouse);
}

public interface IParcelStatusService
{
    Task<ParcelStatusUpdateResponse> UpdateAsync(Guid parcelId, UpdateParcelStatusRequest request, CancellationToken cancellationToken = default);

    Task<ParcelStatusUpdateResponse> UpdateByTrackingNumberAsync(string trackingNumber, UpdateParcelStatusRequest request, CancellationToken cancellationToken = default);

    Task<ParcelStatusUpdateResponse> ScanAsync(ScanRequest request, CancellationToken cancellationToken = default);

    Task<BatchScanResponse> ScanBatchAsync(BatchScanRequest request, CancellationToken cancellationToken = default);
}

public interface ITrackingService
{
    Task<PublicTrackingResponse> GetPublicAsync(string trackingNumber, CancellationToken cancellationToken = default);

    Task<TrackingOtpRequestedResponse> RequestOtpAsync(string trackingNumber, CancellationToken cancellationToken = default);

    Task<TrackingAccessResponse> VerifyOtpAsync(string trackingNumber, VerifyTrackingOtpRequest request, CancellationToken cancellationToken = default);

    /// <param name="accessToken">The <c>X-Tracking-Access</c> token from a verified receiver, if any. Without one the caller must be signed in.</param>
    Task<DetailedTrackingResponse> GetDetailsAsync(string trackingNumber, string? accessToken, CancellationToken cancellationToken = default);
}

public interface IWarehouseService
{
    Task<PagedResponse<WarehouseResponse>> GetPageAsync(PageQuery query, CancellationToken cancellationToken = default);

    Task<WarehouseResponse> CreateAsync(CreateWarehouseRequest request, CancellationToken cancellationToken = default);
}
