using System.Security.Cryptography;
using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Branches;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Common.Validation;
using NepalParcel.Application.Parcels;
using NepalParcel.Contracts.Tracking;
using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Notifications;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Tracking;

namespace NepalParcel.Application.Tracking;

/// <summary>
/// Public and detailed tracking. The public view is limited to what is safe to show anyone who knows the tracking number;
/// the detailed view needs a receiver verified by a one-time code, or a signed-in sender or staff member.
/// </summary>
internal sealed partial class TrackingService(
    IParcelRepository parcels,
    IBranchRepository branches,
    IWarehouseRepository warehouses,
    ICustomerRepository customers,
    ITrackingOtpRepository otps,
    INotificationOutboxRepository outbox,
    IAuditLogRepository auditLogs,
    ITrackingSecurity security,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IClientInfo client,
    TimeProvider clock,
    IOptions<TrackingOptions> options,
    IValidator<VerifyTrackingOtpRequest> verifyValidator,
    ILogger<TrackingService> logger) : ITrackingService
{
    private const int CodeLength = 6;
    private const string OtpTemplateKey = "tracking-otp";

    public async Task<PublicTrackingResponse> GetPublicAsync(string trackingNumber, CancellationToken cancellationToken = default)
    {
        var parcel = await FindAsync(trackingNumber, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Parcel", TrackingNumbers.Normalize(trackingNumber));

        var names = await LoadLocationNamesAsync(parcel, cancellationToken).ConfigureAwait(false);
        var status = parcel.Status;
        return new PublicTrackingResponse(
            parcel.TrackingNumber,
            status.ToString(),
            ParcelStatusCatalog.Get(status).Name,
            ParcelStatusCatalog.Get(status).PublicMessage,
            parcel.ServiceType.ToString(),
            names.OriginArea,
            parcel.ReceiverAddress.District,
            parcel.BookedAtUtc,
            parcel.EstimatedDeliveryAtUtc,
            parcel.DeliveredAtUtc,
            ParcelStatusWorkflow.IsTerminal(status),
            Timeline(parcel, names.Locations).Select(e => new TrackingEventDto(e.Status, e.StatusName, e.Message, e.OccurredAtUtc, e.Location)).ToList());
    }

    public async Task<TrackingOtpRequestedResponse> RequestOtpAsync(string trackingNumber, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var parcel = await FindAsync(trackingNumber, cancellationToken).ConfigureAwait(false);
        if (parcel is not null)
        {
            var now = clock.GetUtcNow();
            var recent = await otps.CountSinceAsync(parcel.Id, now.AddHours(-1), cancellationToken).ConfigureAwait(false);
            if (recent >= settings.MaxRequestsPerHour)
            {
                // Ignored without telling the caller, so this endpoint reveals nothing and cannot be used to spam the receiver.
                LogOtpThrottled(logger, parcel.Id);
            }
            else
            {
                var code = NewCode();
                otps.Add(TrackingOtp.Create(parcel.Id, security.HashOtp(parcel.Id, code), now, TimeSpan.FromMinutes(settings.OtpLifetimeMinutes)));
                outbox.Add(NotificationOutboxMessage.Enqueue(
                    NotificationChannel.Sms,
                    parcel.ReceiverPhone,
                    OtpTemplateKey,
                    JsonSerializer.Serialize(new { code, trackingNumber = parcel.TrackingNumber, validForMinutes = settings.OtpLifetimeMinutes }),
                    now,
                    parcel.Id));
                auditLogs.Add(AuditLog.Create(
                    AuditActions.TrackingOtpRequested,
                    "Parcel",
                    parcel.Id.ToString(),
                    currentUser.UserId,
                    now,
                    ipAddress: client.IpAddress,
                    userAgent: client.UserAgent,
                    correlationId: client.CorrelationId));
                await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                if (settings.LogCodes)
                {
                    LogDevelopmentCode(logger, parcel.TrackingNumber, code);
                }
            }
        }

        return new TrackingOtpRequestedResponse(
            "If this tracking number is valid, a verification code has been sent to the receiver's phone number.",
            settings.OtpLifetimeMinutes);
    }

    public async Task<TrackingAccessResponse> VerifyOtpAsync(string trackingNumber, VerifyTrackingOtpRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await verifyValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var settings = options.Value;
        var now = clock.GetUtcNow();

        // Every failure looks the same: unknown number, no code requested, expired, locked or simply wrong.
        var failure = new RequestValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Code"] = ["The code is incorrect or has expired. Request a new code and try again."],
        });

        var parcel = await FindAsync(trackingNumber, cancellationToken).ConfigureAwait(false) ?? throw failure;
        var otp = await otps.GetLatestAsync(parcel.Id, cancellationToken).ConfigureAwait(false);
        if (otp is null || !otp.IsUsable(now, settings.MaxFailedAttempts))
        {
            throw failure;
        }

        if (!security.VerifyOtp(parcel.Id, request.Code, otp.CodeHash))
        {
            otp.RegisterFailedAttempt();
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            throw failure;
        }

        otp.Consume(now);
        auditLogs.Add(AuditLog.Create(
            AuditActions.TrackingOtpVerified,
            "Parcel",
            parcel.Id.ToString(),
            null,
            now,
            ipAddress: client.IpAddress,
            userAgent: client.UserAgent,
            correlationId: client.CorrelationId));
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var expires = now.AddMinutes(settings.AccessTokenMinutes);
        return new TrackingAccessResponse(security.CreateAccessToken(parcel.TrackingNumber, expires), expires);
    }

    public async Task<DetailedTrackingResponse> GetDetailsAsync(string trackingNumber, string? accessToken, CancellationToken cancellationToken = default)
    {
        var normalized = TrackingNumbers.Normalize(trackingNumber);
        bool includeRemarks;
        Parcel? parcel;

        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            // A receiver who proved themselves with a code. The token is bound to this one tracking number.
            if (!security.ValidateAccessToken(accessToken, normalized, clock.GetUtcNow()))
            {
                throw new UnauthorizedException("The verification is invalid or has expired. Please verify again.");
            }

            parcel = await FindAsync(normalized, cancellationToken).ConfigureAwait(false)
                ?? throw new NotFoundException("Parcel", normalized);
            includeRemarks = false;
        }
        else if (currentUser.IsAuthenticated)
        {
            parcel = await FindAsync(normalized, cancellationToken).ConfigureAwait(false)
                ?? throw new NotFoundException("Parcel", normalized);
            await EnsureSignedInUserMayViewAsync(parcel, cancellationToken).ConfigureAwait(false);
            includeRemarks = true;
        }
        else
        {
            throw new UnauthorizedException("Verify with the one-time code sent to the receiver's phone, or sign in, to see detailed tracking.");
        }

        var names = await LoadLocationNamesAsync(parcel, cancellationToken).ConfigureAwait(false);
        var status = parcel.Status;
        var address = parcel.ReceiverAddress;
        return new DetailedTrackingResponse(
            parcel.TrackingNumber,
            status.ToString(),
            ParcelStatusCatalog.Get(status).Name,
            ParcelStatusCatalog.Get(status).PublicMessage,
            parcel.ServiceType.ToString(),
            names.OriginArea,
            address.District,
            parcel.ReceiverName,
            $"{address.Municipality}-{address.WardNumber}, {address.District}",
            parcel.CodAmount,
            parcel.PaymentMethod.ToString(),
            parcel.BookedAtUtc,
            parcel.EstimatedDeliveryAtUtc,
            parcel.DeliveredAtUtc,
            ParcelStatusWorkflow.IsTerminal(status),
            Timeline(parcel, names.Locations)
                .Select(e => new DetailedTrackingEventDto(e.Status, e.StatusName, e.Message, e.OccurredAtUtc, e.Location, includeRemarks ? e.Remarks : null))
                .ToList());
    }

    private async Task<Parcel?> FindAsync(string trackingNumber, CancellationToken cancellationToken)
    {
        var normalized = TrackingNumbers.Normalize(trackingNumber);
        return TrackingNumbers.IsValid(normalized)
            ? await parcels.GetByTrackingNumberAsync(normalized, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private async Task EnsureSignedInUserMayViewAsync(Parcel parcel, CancellationToken cancellationToken)
    {
        if (Roles.ParcelViewers.Any(currentUser.IsInRole))
        {
            return;
        }

        // Senders see their own parcels. Anyone else gets the same answer as for a parcel that does not exist.
        var customer = currentUser.UserId is { } userId
            ? await customers.GetByUserIdAsync(userId, cancellationToken).ConfigureAwait(false)
            : null;
        if (customer is null || customer.Id != parcel.SenderCustomerId)
        {
            throw new NotFoundException("Parcel", parcel.TrackingNumber);
        }
    }

    private sealed record Names(string OriginArea, IReadOnlyDictionary<Guid, string> Locations);

    private async Task<Names> LoadLocationNamesAsync(Parcel parcel, CancellationToken cancellationToken)
    {
        var branchIds = parcel.StatusHistory.Select(h => h.BranchId).Append(parcel.OriginBranchId).OfType<Guid>().Distinct().ToList();
        var warehouseIds = parcel.StatusHistory.Select(h => h.WarehouseId).OfType<Guid>().Distinct().ToList();

        var foundBranches = await branches.GetByIdsAsync(branchIds, cancellationToken).ConfigureAwait(false);
        var foundWarehouses = warehouseIds.Count == 0
            ? new Dictionary<Guid, NepalParcel.Domain.Warehouses.Warehouse>()
            : (await warehouses.GetByIdsAsync(warehouseIds, cancellationToken).ConfigureAwait(false)).ToDictionary(kv => kv.Key, kv => kv.Value);

        var names = new Dictionary<Guid, string>();
        foreach (var (id, branch) in foundBranches)
        {
            names[id] = branch.Name;
        }

        foreach (var (id, warehouse) in foundWarehouses)
        {
            names[id] = warehouse.Name;
        }

        var origin = foundBranches.TryGetValue(parcel.OriginBranchId, out var originBranch) ? originBranch.Address.District : "Nepal";
        return new Names(origin, names);
    }

    private sealed record TimelineEvent(string Status, string StatusName, string Message, DateTimeOffset OccurredAtUtc, string? Location, string? Remarks);

    private static List<TimelineEvent> Timeline(Parcel parcel, IReadOnlyDictionary<Guid, string> locations) =>
        parcel.StatusHistory
            .OrderBy(h => h.OccurredAtUtc)
            .Select(h =>
            {
                var code = (ParcelStatusCode)h.ToStatusId;
                var entry = ParcelStatusCatalog.Get(code);
                var place = h.WarehouseId is { } w && locations.TryGetValue(w, out var wn) ? wn
                    : h.BranchId is { } b && locations.TryGetValue(b, out var bn) ? bn
                    : null;
                return new TimelineEvent(code.ToString(), entry.Name, entry.PublicMessage, h.OccurredAtUtc, place, h.Remarks);
            })
            .ToList();

    private static string NewCode() =>
        RandomNumberGenerator.GetInt32(0, (int)Math.Pow(10, CodeLength)).ToString(new string('0', CodeLength), System.Globalization.CultureInfo.InvariantCulture);

    [LoggerMessage(EventId = 9101, Level = LogLevel.Warning, Message = "Tracking code request for parcel {ParcelId} ignored: hourly limit reached")]
    private static partial void LogOtpThrottled(ILogger logger, Guid parcelId);

    [LoggerMessage(EventId = 9102, Level = LogLevel.Warning, Message = "DEVELOPMENT ONLY: tracking code for {TrackingNumber} is {Code}")]
    private static partial void LogDevelopmentCode(ILogger logger, string trackingNumber, string code);
}
